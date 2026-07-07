using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Microsoft.Extensions.Logging;

namespace Application.Core.Permissions;

/// <summary>
/// Emits <see cref="ToolPermissionRule"/> entries derived from plugin declarations that
/// specify an autonomy level override. Rules feed into the existing 3-phase permission
/// resolver alongside agent-level autonomy tier rules.
/// </summary>
/// <remarks>
/// Any <c>DeniedTools</c> declared on a loaded plugin emit Deny rules at a lower priority value
/// (checked first) and are marked <c>IsBypassImmune</c> so they cannot be overridden by
/// auto-approve modes. These deny rules are emitted <b>regardless</b> of whether the plugin also
/// sets an <c>AutonomyLevel</c> — the deny boundary is independent of the autonomy override.
/// Additionally, each loaded plugin whose <c>AutonomyLevel</c> is set contributes one baseline
/// rule scoped to <c>{pluginName}:*</c>.
/// </remarks>
public sealed class PluginPermissionRuleProvider : IPermissionRuleProvider
{
    private readonly IPluginRegistry _registry;
    private readonly ILogger<PluginPermissionRuleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPermissionRuleProvider"/> class.
    /// </summary>
    /// <param name="registry">The plugin registry providing loaded plugin metadata.</param>
    /// <param name="logger">Logger for invalid autonomy level warnings.</param>
    public PluginPermissionRuleProvider(
        IPluginRegistry registry,
        ILogger<PluginPermissionRuleProvider> logger)
    {
        _registry = registry;
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

            rules.Add(new ToolPermissionRule(
                $"{plugin.Name}:*",
                null,
                defaultBehavior,
                PermissionRuleSource.PluginDeclaration,
                Priority: 5));
        }

        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(rules);
    }
}
