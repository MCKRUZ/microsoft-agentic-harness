using Domain.AI.Context;

namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Stores large tool results to disk and returns references with previews.
/// Results below the size threshold are returned as-is without disk persistence.
/// </summary>
public interface IToolResultStore
{
    /// <summary>
    /// Stores a tool result if it exceeds the configured size limit.
    /// Small results are returned with full content as the preview (no disk write).
    /// Large results are persisted to disk with a truncated preview.
    /// </summary>
    /// <param name="sessionId">The current session identifier for organizing stored results.</param>
    /// <param name="toolName">The name of the tool that produced the result.</param>
    /// <param name="operation">The specific operation within the tool, if applicable.</param>
    /// <param name="fullOutput">The complete tool output to evaluate and potentially store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ToolResultReference"/> containing either the full content as a preview
    /// (for small results) or a truncated preview with a disk path (for large results).
    /// </returns>
    Task<ToolResultReference> StoreIfLargeAsync(
        string sessionId,
        string toolName,
        string? operation,
        string fullOutput,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full content of a previously persisted result.
    /// </summary>
    /// <param name="resultId">The unique identifier of the stored result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full content that was persisted to disk.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="resultId"/> is not found.</exception>
    Task<string> RetrieveFullContentAsync(
        string resultId,
        CancellationToken cancellationToken = default);
}
