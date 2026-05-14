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

    /// <summary>Queries audit records matching the specified filters.</summary>
    Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(DriftAuditQuery query, CancellationToken ct);
}
