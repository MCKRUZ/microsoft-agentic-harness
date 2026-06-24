using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Resolves and assembles tools for agent execution contexts. Supports three resolution
/// modes — Injected (all MCP tools passed through), Managed with ToolDeclarations (MCP-first
/// with keyed DI fallback), and Managed with AllowedTools (simple name-based resolution).
/// Applies plugin governance boundary filtering (AllowedTools/DeniedTools) for plugin-sourced skills.
/// </summary>
public class ToolChainBuilder : IToolChainBuilder
{
    private readonly ILogger<ToolChainBuilder> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IToolConverter? _toolConverter;
    private readonly IMcpToolProvider? _mcpToolProvider;

    public ToolChainBuilder(
        ILogger<ToolChainBuilder> logger,
        IServiceProvider serviceProvider,
        IToolConverter? toolConverter = null,
        IMcpToolProvider? mcpToolProvider = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _toolConverter = toolConverter;
        _mcpToolProvider = mcpToolProvider;
    }

    /// <inheritdoc />
    public Task<List<AITool>> BuildToolsAsync(SkillDefinition skill, SkillAgentOptions options, CancellationToken cancellationToken = default)
        // Public callers don't need MCP attribution — use a throwaway collector so
        // resolution paths still record where each tool came from but the result is
        // discarded.
        => BuildToolsAsync(skill, options, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken);

    private async Task<List<AITool>> BuildToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        ISet<string> mcpCollector,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>();

        if (skill.Mode == SkillMode.Injected && _mcpToolProvider != null)
        {
            var allMcpTools = await _mcpToolProvider.GetAllToolsAsync(cancellationToken);
            foreach (var serverTools in allMcpTools.Values)
            {
                tools.AddRange(serverTools);
                foreach (var t in serverTools) mcpCollector.Add(t.Name);
            }

            if (options.AdditionalTools?.Count > 0)
                tools.AddRange(options.AdditionalTools);

            if (!string.IsNullOrEmpty(skill.PluginSource))
            {
                var pluginRegistry = _serviceProvider.GetService<IPluginRegistry>();
                var loadedPlugin = pluginRegistry?.GetPlugin(skill.PluginSource);
                if (loadedPlugin != null)
                    tools = ApplyPluginToolBoundary(tools, loadedPlugin.Declaration);
            }

            _logger.LogInformation(
                "Injected mode: skill {SkillId} from plugin {Plugin} received {Count} MCP tools",
                skill.Id, skill.PluginSource, tools.Count);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return WrapGoverned(tools.Where(t => seen.Add(t.Name)));
        }

        if (skill.Tools?.Count > 0)
            tools.AddRange(skill.Tools);

        if (skill.ToolDeclarations?.Count > 0)
        {
            var provisionTasks = skill.ToolDeclarations.Select(d => ProvisionToolAsync(d, mcpCollector, cancellationToken));
            var results = await Task.WhenAll(provisionTasks);
            foreach (var provisioned in results)
            {
                if (provisioned != null)
                    tools.AddRange(provisioned);
            }
        }

        if (skill.AllowedTools?.Count > 0)
        {
            foreach (var toolName in skill.AllowedTools)
            {
                var resolved = ResolveToolByName(toolName);
                if (resolved != null)
                    tools.AddRange(resolved);
            }
        }

        if (options.AdditionalTools?.Count > 0)
            tools.AddRange(options.AdditionalTools);

        var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return WrapGoverned(tools.Where(t => seen2.Add(t.Name)));
    }

    /// <summary>
    /// Wraps each callable tool function in a <see cref="GovernedAIFunction"/> so a per-invocation
    /// governance check runs before the tool executes. Non-function tools and already-wrapped
    /// functions pass through unchanged. The wrapper is inert unless tool-invocation enforcement is
    /// enabled and a governor is ambient for the turn, so this adds no behaviour when governance is off.
    /// Applied at this single shared builder so every agent-callable tool — keyed-DI, MCP, or
    /// skill-provided — is governed exactly once.
    /// </summary>
    private static List<AITool> WrapGoverned(IEnumerable<AITool> tools)
        => tools
            .Select(t => t is AIFunction fn and not GovernedAIFunction ? new GovernedAIFunction(fn) : t)
            .ToList();

    /// <inheritdoc />
    public async Task<List<AITool>> BuildMergedToolsAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default)
    {
        var merged = await BuildMergedToolsWithSourcesAsync(skills, options, allowedTools, cancellationToken);
        return merged.Tools.ToList();
    }

    /// <inheritdoc />
    public async Task<MergedToolChain> BuildMergedToolsWithSourcesAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default)
    {
        // MCP-sourced tool names accumulate as resolution happens — no extra round trip.
        // Injected-mode skills contribute every MCP tool; managed-mode skills contribute
        // only tools whose ToolDeclaration was satisfied by MCP first.
        var mcpCollector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allTools = new List<AITool>();
        foreach (var skill in skills)
        {
            var skillTools = await BuildToolsAsync(skill, options, mcpCollector, cancellationToken);
            allTools.AddRange(skillTools);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = allTools.Where(t => seen.Add(t.Name)).ToList();

        if (allowedTools is { Count: > 0 })
        {
            var allowed = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
            deduplicated = deduplicated.Where(t => allowed.Contains(t.Name)).ToList();
        }

        // Filter MCP names down to what actually survived dedup + AllowedTools so the
        // panel doesn't claim a tool was MCP-sourced when it was governance-filtered out.
        var survivingNames = new HashSet<string>(deduplicated.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var attributedMcp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in mcpCollector)
            if (survivingNames.Contains(name))
                attributedMcp.Add(name);

        return new MergedToolChain(deduplicated, attributedMcp);
    }

    internal static List<AITool> ApplyPluginToolBoundary(List<AITool> tools, PluginDeclaration declaration)
    {
        if (declaration.AllowedTools is { Count: > 0 } allowed)
        {
            var allowSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => allowSet.Contains(t.Name)).ToList();
        }

        if (declaration.DeniedTools is { Count: > 0 } denied)
        {
            var denySet = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => !denySet.Contains(t.Name)).ToList();
        }

        return tools;
    }

    private async Task<IEnumerable<AITool>?> ProvisionToolAsync(
        Domain.AI.Tools.ToolDeclaration declaration,
        ISet<string> mcpCollector,
        CancellationToken cancellationToken = default)
    {
        if (_mcpToolProvider != null)
        {
            try
            {
                var mcpTools = await _mcpToolProvider.GetToolsAsync(declaration.Name, cancellationToken);
                if (mcpTools?.Count > 0)
                {
                    _logger.LogDebug("Resolved tool {ToolName} from MCP server", declaration.Name);
                    foreach (var t in mcpTools) mcpCollector.Add(t.Name);
                    return mcpTools;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP resolution failed for {ToolName}, trying keyed DI", declaration.Name);
            }
        }

        var resolved = ResolveToolByName(declaration.Name);
        if (resolved != null)
            return resolved;

        if (declaration.HasFallback && !declaration.FallbackIsManual)
        {
            resolved = ResolveToolByName(declaration.Fallback!);
            if (resolved != null)
            {
                _logger.LogInformation("Using fallback tool {Fallback} for {ToolName}",
                    declaration.Fallback, declaration.Name);
                return resolved;
            }
        }

        if (!declaration.Optional && !declaration.FallbackIsManual)
        {
            throw new InvalidOperationException(
                $"Required tool '{declaration.Name}' could not be resolved. " +
                "Ensure the tool is registered via keyed DI or available from an MCP server. " +
                "Mark the tool declaration as Optional = true if the skill can function without it.");
        }

        return null;
    }

    private IEnumerable<AITool>? ResolveToolByName(string toolName)
    {
        var tool = _serviceProvider.GetKeyedService<ITool>(toolName);
        if (tool == null)
            return null;

        if (_toolConverter != null)
        {
            var converted = _toolConverter.Convert(tool);
            if (converted != null)
                return [converted];
        }

        _logger.LogWarning("Tool {ToolName} found in keyed DI but no IToolConverter available to convert it", toolName);
        return [];
    }
}
