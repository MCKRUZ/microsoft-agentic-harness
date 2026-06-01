using Application.AI.Common.Prompts.Models;

namespace Application.AI.Common.Prompts.Interfaces;

/// <summary>
/// Durable store for <see cref="PromptUsageRecord"/>s emitted by
/// <see cref="IPromptUsageRecorder"/> implementations. Provides the persistence
/// layer that trace-replay (Sub-phase 5.3b Step 5) queries against to recover
/// which prompt version produced a given case or trace.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be append-only at the public surface: records are
/// immutable once written. There is no Update/Delete; correcting an erroneous
/// row is done by writing a new compensating record (out of scope today).
/// </para>
/// <para>
/// Implementations should be safe for concurrent appends — multiple handlers
/// may write simultaneously. Query methods need not be transactional but should
/// return a consistent snapshot for any single (trace_id, case_id) tuple.
/// </para>
/// </remarks>
public interface IPromptUsageStore
{
    /// <summary>
    /// Appends a record to the store. Idempotency is the caller's responsibility —
    /// duplicate appends produce duplicate rows; the store does not deduplicate.
    /// </summary>
    Task AppendAsync(PromptUsageRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every record stamped with the supplied OTel/W3C trace id, ordered
    /// by <see cref="PromptUsageRecord.RecordedAtUtc"/> ascending. Returns an empty
    /// list when the trace id is unknown.
    /// </summary>
    Task<IReadOnlyList<PromptUsageRecord>> QueryByTraceIdAsync(string traceId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every record attributed to the supplied case id, ordered by
    /// <see cref="PromptUsageRecord.RecordedAtUtc"/> ascending. Returns an empty
    /// list when the case id is unknown.
    /// </summary>
    Task<IReadOnlyList<PromptUsageRecord>> QueryByCaseIdAsync(string caseId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every record for the supplied prompt name, across all versions and
    /// cases, ordered by <see cref="PromptUsageRecord.RecordedAtUtc"/> ascending.
    /// Returns an empty list when the prompt name is unknown.
    /// </summary>
    /// <remarks>
    /// Powers Sub-phase 5.4.3's prompt-version comparison query: the dashboard
    /// needs every (version, case_id, metric_key) tuple for a prompt so it can
    /// aggregate eval scores per version. Case-insensitive match on
    /// <see cref="Domain.AI.Prompts.PromptDescriptor.Name"/>.
    /// </remarks>
    Task<IReadOnlyList<PromptUsageRecord>> QueryByPromptNameAsync(
        string promptName,
        CancellationToken cancellationToken);
}
