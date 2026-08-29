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
    /// <param name="sizeThreshold">
    /// The size, in characters, above which <paramref name="fullOutput"/> must be persisted — compared
    /// in place of the store's own configured size limit when supplied. A caller that already cut
    /// <paramref name="fullOutput"/>'s model-facing copy to a ceiling smaller than that configured
    /// limit — <c>ToolCallAdmissionPipeline</c>'s aggregate per-message budget shrinks the ceiling a
    /// single result is cut to below <c>PerResultCharLimit</c> (#522) — must pass that same ceiling
    /// here. Re-deriving the spill decision from a threshold the caller's own cut never used would
    /// silently judge a perfectly normal-sized result "too small to bother spilling" and leave a
    /// truncated, unrecoverable result with no retrieval id. The comparison still runs either way — a
    /// caller can shrink the threshold that applies, never bypass the check outright. Defaults to
    /// <see langword="null"/> so a caller with no ceiling of its own keeps today's size-based behavior,
    /// compared against the store's own configured limit.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ToolResultReference"/> containing either the full content as a preview (when
    /// <paramref name="fullOutput"/> is at or under whichever threshold applied) or a truncated preview
    /// with a disk path (when it exceeds that threshold).
    /// </returns>
    Task<ToolResultReference> StoreIfLargeAsync(
        string sessionId,
        string toolName,
        string? operation,
        string fullOutput,
        int? sizeThreshold = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full content of a previously persisted result, enforced against the scope it was
    /// stored under (#521).
    /// </summary>
    /// <param name="resultId">The unique identifier of the stored result.</param>
    /// <param name="scopeId">
    /// The caller's own isolation boundary — <see cref="Agent.IAgentExecutionContext.ToolResultScopeId"/>
    /// in production. Must match the <c>sessionId</c> <see cref="StoreIfLargeAsync"/> was called with
    /// when this result was stored, or retrieval is refused. Enforced <em>here</em>, in the store, not
    /// left to each call site — the same rule <c>IConversationStore</c> applies to conversation
    /// ownership, for the identical reason: a check a caller could forget is not a check.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full content that was persisted to disk.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="resultId"/> is not found <em>under <paramref name="scopeId"/></em> —
    /// indistinguishable from "does not exist at all" on purpose. A result that exists under a
    /// different scope must read exactly like one that was never stored; a distinct error for "exists,
    /// but not yours" would tell an unauthorized caller that guessing worked, which is the same
    /// information a Denied vs. NotFound split would leak on a conversation lookup.
    /// </exception>
    Task<string> RetrieveFullContentAsync(
        string resultId,
        string scopeId,
        CancellationToken cancellationToken = default);
}
