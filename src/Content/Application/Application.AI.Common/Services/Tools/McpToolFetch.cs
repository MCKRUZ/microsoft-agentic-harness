using Application.AI.Common.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Fetches one MCP server's tools, treating an unreachable server as "contributes nothing" rather than
/// a failed operation. Shared by <see cref="ToolChainBuilder"/> (fetching tools to publish) and
/// <c>BundleRunExecutor</c> (fetching tools to discover grant names) so both apply the exact same
/// fail-soft policy instead of maintaining independent copies of the same try/catch.
/// </summary>
public static class McpToolFetch
{
    /// <summary>
    /// Fetches <paramref name="serverName"/>'s tools via <paramref name="mcpToolProvider"/>. Returns
    /// <see langword="null"/> — logged, never thrown — when the server could not be reached, so a single
    /// unreachable server never fails the caller's larger operation (a tool build, or a run's grant
    /// discovery).
    /// </summary>
    /// <param name="context">Short caller-identifying prefix for the log message, e.g. <c>"Capability envelope"</c>.</param>
    public static async Task<IList<AITool>?> TryGetToolsAsync(
        IMcpToolProvider mcpToolProvider, string serverName, string context, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await mcpToolProvider.GetToolsAsync(serverName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Context}: MCP server '{Server}' could not be reached — skipped", context, serverName);
            return null;
        }
    }
}
