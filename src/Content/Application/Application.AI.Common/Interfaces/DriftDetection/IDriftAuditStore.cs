using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Append-only persistence for drift audit records.
/// Supports JSONL-backed storage for compliance and debugging.
/// </summary>
public interface IDriftAuditStore
{
    /// <summary>Appends an audit record to the store.</summary>
    Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct);

    /// <summary>
    /// Appends a record that must be in the trail before the action it attributes runs (an operator's attempt record): when
    /// it cannot be written the caller refuses the action, so the record is never held for replay (#1001). Held, it would
    /// attribute a mutation that never ran.
    /// </summary>
    /// <param name="record">The record to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success when the record is in the trail; a failure the caller must refuse its action on.</returns>
    Task<Result> RecordBeforeActingAsync(DriftAuditRecord record, CancellationToken ct);

    /// <summary>Queries audit records matching the specified filters.</summary>
    Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(DriftAuditQuery query, CancellationToken ct);
}
