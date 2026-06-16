using Domain.AI.Skills;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Resolves and assembles tools for agent execution contexts. Handles three resolution
/// modes (Injected, ToolDeclarations, AllowedTools), MCP-first resolution with keyed DI
/// fallback, plugin governance boundary filtering, and cross-skill deduplication.
/// </summary>
public interface IToolChainBuilder
{
    /// <summary>
    /// Resolves tools for a single skill using the appropriate resolution mode.
    /// </summary>
    /// <param name="skill">The skill definition containing tool declarations and mode.</param>
    /// <param name="options">Options providing additional tools and overrides.</param>
    /// <param name="cancellationToken">
    /// Cancels tool resolution. Implementations perform network I/O against MCP servers, so a
    /// hung or slow server must not block agent construction past caller cancellation.
    /// </param>
    /// <returns>A deduplicated list of resolved tools.</returns>
    Task<List<AITool>> BuildToolsAsync(
        SkillDefinition skill,
        SkillAgentOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges and deduplicates tools from multiple skills, applying an optional whitelist.
    /// First occurrence wins during deduplication.
    /// </summary>
    /// <param name="skills">The skill definitions to merge tools from.</param>
    /// <param name="options">Options providing additional tools and overrides.</param>
    /// <param name="allowedTools">Optional tool allowlist — only tools with matching names are kept.</param>
    /// <param name="cancellationToken">
    /// Cancels tool resolution. Implementations perform network I/O against MCP servers, so a
    /// hung or slow server must not block agent construction past caller cancellation.
    /// </param>
    /// <returns>A deduplicated, optionally filtered list of resolved tools.</returns>
    Task<List<AITool>> BuildMergedToolsAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="BuildMergedToolsAsync"/>, but also returns the names of tools
    /// that were sourced from an MCP server. Used by <c>AgentExecutionContextFactory</c>
    /// to populate <c>AgentExecutionContext.McpToolNames</c> so downstream code can
    /// attribute each tool to its origin without re-querying the MCP provider.
    /// </summary>
    /// <param name="skills">The skill definitions to merge tools from.</param>
    /// <param name="options">Options providing additional tools and overrides.</param>
    /// <param name="allowedTools">Optional tool allowlist — only tools with matching names are kept.</param>
    /// <param name="cancellationToken">
    /// Cancels tool resolution. Implementations perform network I/O against MCP servers, so a
    /// hung or slow server must not block agent construction past caller cancellation.
    /// </param>
    Task<MergedToolChain> BuildMergedToolsWithSourcesAsync(
        IReadOnlyList<SkillDefinition> skills,
        SkillAgentOptions options,
        IReadOnlyList<string>? allowedTools = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IToolChainBuilder.BuildMergedToolsWithSourcesAsync"/>:
/// the resolved tool chain plus the set of tool names attributable to MCP.
/// </summary>
public sealed record MergedToolChain(
    IReadOnlyList<AITool> Tools,
    IReadOnlySet<string> McpToolNames);
