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
    /// Retrieves one bounded page of a previously persisted result, enforced against the scope it was
    /// stored under (#521), starting at <paramref name="offset"/> and reading up to
    /// <paramref name="maxChars"/> characters.
    /// </summary>
    /// <remarks>
    /// There is deliberately no whole-file read on this interface (#563). The stored copy is the
    /// tool's raw, untreated output — up to <c>ToolResultStorageConfig.MaxSpillChars</c> characters —
    /// so a caller must sanitize, redact, and bound whatever a page returns before it reaches a model,
    /// exactly as it would for any other tool result; a whole-file read would tempt a caller to hand an
    /// unscanned, unbounded string onward instead. Paging also lets each read stay a bounded scan
    /// regardless of how large the stored result is, which a whole-file read could not offer without
    /// scanning the whole thing first.
    /// </remarks>
    /// <param name="resultId">The unique identifier of the stored result.</param>
    /// <param name="scopeId">
    /// The caller's own isolation boundary — <see cref="Agent.IAgentExecutionContext.ToolResultScopeId"/>
    /// in production. Must match the <c>sessionId</c> <see cref="StoreIfLargeAsync"/> was called with
    /// when this result was stored, or retrieval is refused. Enforced <em>here</em>, in the store, not
    /// left to each call site — the same rule <c>IConversationStore</c> applies to conversation
    /// ownership, for the identical reason: a check a caller could forget is not a check.
    /// </param>
    /// <param name="offset">
    /// The character offset to start reading from — <see cref="ToolResultPage.NextOffset"/> from a
    /// prior page, or <c>0</c> for the first page. An offset at or beyond the stored length returns an
    /// empty page with <see cref="ToolResultPage.HasMore"/> false, not an error — the caller may not
    /// know the exact length in advance.
    /// </param>
    /// <param name="maxChars">The maximum number of characters to return in this page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The requested page.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="resultId"/> is not found <em>under <paramref name="scopeId"/></em> —
    /// indistinguishable from "does not exist at all" on purpose. A result that exists under a
    /// different scope must read exactly like one that was never stored; a distinct error for "exists,
    /// but not yours" would tell an unauthorized caller that guessing worked, which is the same
    /// information a Denied vs. NotFound split would leak on a conversation lookup.
    /// </exception>
    Task<ToolResultPage> RetrievePageAsync(
        string resultId,
        string scopeId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every spilled result older than <paramref name="gracePeriod"/> (#559).
    /// </summary>
    /// <remarks>
    /// Nothing else ever removes a spilled file — <see cref="StoreIfLargeAsync"/> only ever adds one,
    /// and a scope whose owning conversation or run has long since ended leaves no other signal behind
    /// that its files are safe to reclaim. Age is a coarser test than "the owning scope is gone" would
    /// be, but the store has no way to ask that question — it does not know what a conversation or a
    /// plan run is — and unlike <c>ConversationBudgetRetentionConfig</c>'s reasoning against an
    /// age-only rule (deleting a budget row resets a ceiling), deleting a stale spilled result merely
    /// means a very old <c>tool_result_fetch</c> call fails instead of succeeding — a retrieval
    /// convenience, not an enforcement boundary.
    /// </remarks>
    /// <param name="gracePeriod">
    /// How long a spilled file must sit untouched, by last-write time, before it is reclaimed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of files removed.</returns>
    Task<int> PruneExpiredAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default);
}
