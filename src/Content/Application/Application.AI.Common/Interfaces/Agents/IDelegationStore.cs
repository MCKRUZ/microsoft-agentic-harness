using Domain.AI.Orchestration;

namespace Application.AI.Common.Interfaces.Agents;

/// <summary>
/// Persists delegation records as append-only JSONL per supervisor session.
/// </summary>
public interface IDelegationStore
{
    /// <summary>Appends a delegation record to the session file.</summary>
    Task AppendAsync(DelegationRecord record, CancellationToken ct = default);

    /// <summary>Returns the latest record for the given delegation ID, or null if not found.</summary>
    Task<DelegationRecord?> GetByIdAsync(Guid delegationId, CancellationToken ct = default);

    /// <summary>Returns all records for a supervisor session, deduplicated by delegation ID.</summary>
    Task<IReadOnlyList<DelegationRecord>> GetBySessionAsync(string supervisorId, CancellationToken ct = default);

    /// <summary>Returns all child delegations for the given parent, deduplicated.</summary>
    Task<IReadOnlyList<DelegationRecord>> GetByParentAsync(Guid parentDelegationId, CancellationToken ct = default);
}
