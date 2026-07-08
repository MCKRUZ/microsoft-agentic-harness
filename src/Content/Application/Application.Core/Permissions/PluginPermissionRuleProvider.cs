using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Application.Core.Permissions;

/// <summary>
/// Emits <see cref="ToolPermissionRule"/> entries derived from plugin declarations that
/// specify an autonomy level override. Rules feed into the existing 3-phase permission
/// resolver alongside agent-level autonomy tier rules.
/// </summary>
/// <remarks>
/// <para>
/// Any <c>DeniedTools</c> declared on a loaded plugin emit Deny rules at a lower priority value
/// (checked first) and are marked <c>IsBypassImmune</c> so they cannot be overridden by
/// auto-approve modes. These deny rules are emitted <b>regardless</b> of whether the plugin also
/// sets an <c>AutonomyLevel</c> — the deny boundary is independent of the autonomy override.
/// </para>
/// <para>
/// A plugin's <c>AutonomyLevel</c> is enforced as an <em>authoritative baseline</em> scoped to the
/// plugin's <b>real, declared tool names</b> — never a synthetic <c>{plugin}:*</c> wildcard, which no
/// live tool name ever matches. The provider enumerates every skill attributed to the plugin (via
/// <see cref="SkillDefinition.PluginSource"/>) and collects the tool names those skills declare
/// (<c>AllowedTools</c>, <c>ToolDeclarations</c>, and any pre-created <c>Tools</c>). Each distinct name
/// gets one rule flagged <see cref="ToolPermissionRule.IsAuthoritativeBaseline"/>, so the resolver
/// applies the plugin's autonomy in both directions (Autonomous → Allow can loosen a stricter
/// default; Restricted/Supervised → Ask can tighten) while a bypass-immune <c>DeniedTools</c> rule
/// still wins.
/// </para>
/// <para>
/// <b>Limitation.</b> A plugin whose skills declare no tools (Injected mode — the skill receives all
/// MCP tools at runtime) exposes no statically-enumerable tool names, so its autonomy baseline cannot
/// be scoped to specific tools and is skipped with a warning. Operators who need an autonomy baseline
/// on such a plugin must name its tools via the plugin's <c>AllowedTools</c> or per-skill
/// <c>allowed-tools</c> declarations. (<c>DeniedTools</c> are unaffected — they name tools explicitly.)
/// </para>
/// </remarks>
public sealed class PluginPermissionRuleProvider : IPermissionRuleProvider
{
    private readonly IPluginRegistry _registry;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly ILogger<PluginPermissionRuleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPermissionRuleProvider"/> class.
    /// </summary>
    /// <param name="registry">The plugin registry providing loaded plugin metadata.</param>
    /// <param name="skillRegistry">
    /// The skill metadata registry, used to enumerate the tools declared by a plugin's skills so the
    /// autonomy baseline can be scoped to real tool names.
    /// </param>
    /// <param name="logger">Logger for invalid autonomy level and unscoped-baseline warnings.</param>
    public PluginPermissionRuleProvider(
        IPluginRegistry registry,
        ISkillMetadataRegistry skillRegistry,
        ILogger<PluginPermissionRuleProvider> logger)
    {
        _registry = registry;
        _skillRegistry = skillRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public PermissionRuleSource Source => PermissionRuleSource.PluginDeclaration;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var rules = new List<ToolPermissionRule>();

        foreach (var plugin in _registry.GetLoadedPlugins())
        {
            // DeniedTools are bypass-immune and enforced independently of any AutonomyLevel:
            // a plugin that only denies tools (no autonomy override) must still contribute its
            // Deny rules. Emitted first so the boundary applies even when AutonomyLevel is unset
            // or invalid.
            if (plugin.Declaration.DeniedTools is { Count: > 0 } denied)
            {
                foreach (var deniedTool in denied)
                {
                    rules.Add(new ToolPermissionRule(
                        deniedTool,
                        null,
                        PermissionBehaviorType.Deny,
                        PermissionRuleSource.PluginDeclaration,
                        Priority: 1,
                        IsBypassImmune: true));
                }
            }

            if (string.IsNullOrEmpty(plugin.Declaration.AutonomyLevel))
                continue;

            if (!Enum.TryParse<AutonomyLevel>(plugin.Declaration.AutonomyLevel, ignoreCase: true, out var autonomyLevel))
            {
                _logger.LogWarning(
                    "Plugin {Name}: invalid AutonomyLevel '{Level}', skipping baseline governance rule",
                    plugin.Name, plugin.Declaration.AutonomyLevel);
                continue;
            }

            // Both Restricted and Supervised map to Ask — differentiation is via per-tool
            // overrides in AutonomyTierRuleProvider config, not at the plugin boundary.
            var defaultBehavior = autonomyLevel switch
            {
                AutonomyLevel.Autonomous => PermissionBehaviorType.Allow,
                _ => PermissionBehaviorType.Ask
            };

            var pluginToolNames = EnumeratePluginToolNames(plugin.Name);
            if (pluginToolNames.Count == 0)
            {
                _logger.LogWarning(
                    "Plugin {Name}: AutonomyLevel '{Level}' set, but the plugin's skills declare no tool names " +
                    "(Injected mode) — the autonomy baseline cannot be scoped to specific tools and is skipped. " +
                    "Declare the tools via the plugin's AllowedTools or a skill's allowed-tools to enforce it.",
                    plugin.Name, plugin.Declaration.AutonomyLevel);
                continue;
            }

            foreach (var toolName in pluginToolNames)
            {
                rules.Add(new ToolPermissionRule(
                    toolName,
                    null,
                    defaultBehavior,
                    PermissionRuleSource.PluginDeclaration,
                    Priority: 5,
                    IsAuthoritativeBaseline: true));
            }
        }

        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(rules);
    }

    /// <summary>
    /// Collects the distinct tool names declared by every skill attributed to
    /// <paramref name="pluginName"/>, drawn from each skill's <see cref="SkillDefinition.AllowedTools"/>,
    /// <see cref="SkillDefinition.ToolDeclarations"/>, and pre-created <see cref="SkillDefinition.Tools"/>.
    /// Names are matched at invocation against the live tool set, so they mirror the names the agent
    /// actually calls.
    /// </summary>
    private IReadOnlyCollection<string> EnumeratePluginToolNames(string pluginName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _skillRegistry.GetAll())
        {
            if (!string.Equals(skill.PluginSource, pluginName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (skill.AllowedTools is { Count: > 0 } allowed)
                foreach (var name in allowed)
                    AddIfNamed(names, name);

            if (skill.ToolDeclarations is { Count: > 0 } declarations)
                foreach (var declaration in declarations)
                    AddIfNamed(names, declaration.Name);

            if (skill.Tools is { Count: > 0 } tools)
                foreach (var tool in tools)
                    AddIfNamed(names, tool.Name);
        }

        return names;
    }

    private static void AddIfNamed(HashSet<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            names.Add(name);
    }
}
