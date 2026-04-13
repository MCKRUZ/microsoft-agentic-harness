using Domain.AI.MCP;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Exposes resources via the MCP resource protocol.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are registered as singletons. Providers are composed at the MCP server level
/// and invoked for any URI within their supported scheme.
/// </para>
/// <para>
/// All operations must perform their own auth check via <see cref="McpRequestContext.IsAuthenticated"/>.
/// Throw <see cref="UnauthorizedAccessException"/> for unauthenticated callers.
/// </para>
/// </remarks>
public interface IMcpResourceProvider
{
    /// <summary>
    /// Lists resources available at or beneath the given URI.
    /// </summary>
    /// <param name="uri">The base URI to list, e.g. <c>trace://{runId}/</c>.</param>
    /// <param name="context">The request context carrying the caller's auth principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only list of <see cref="McpResource"/> descriptors. Returns empty when the
    /// URI refers to an unknown or disabled resource set.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">When <paramref name="context"/> is not authenticated.</exception>
    Task<IReadOnlyList<McpResource>> ListAsync(string uri, McpRequestContext context, CancellationToken ct);

    /// <summary>
    /// Reads the content of the resource at the given URI.
    /// </summary>
    /// <param name="uri">The resource URI, e.g. <c>trace://{runId}/eval/task-1/output.json</c>.</param>
    /// <param name="context">The request context carrying the caller's auth principal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="McpResourceContent"/> for the requested resource.</returns>
    /// <exception cref="UnauthorizedAccessException">When <paramref name="context"/> is not authenticated or path traversal is detected.</exception>
    /// <exception cref="FileNotFoundException">When the resource file does not exist.</exception>
    Task<McpResourceContent> ReadAsync(string uri, McpRequestContext context, CancellationToken ct);
}
