using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Persistence contract for EWMA running state.
/// Each scope+identifier+dimension combination has its own state entry.
/// </summary>
public interface IEwmaStateStore
{
    /// <summary>Retrieves EWMA state for a specific dimension. Returns null value when not yet initialized.</summary>
    Task<Result<EwmaState?>> GetStateAsync(DriftScope scope, string scopeIdentifier, DriftDimension dimension, CancellationToken ct);

    /// <summary>Persists updated EWMA state.</summary>
    Task<Result> SaveStateAsync(EwmaState state, CancellationToken ct);

    /// <summary>Retrieves all EWMA states for a scope+identifier (all dimensions).</summary>
    Task<Result<IReadOnlyList<EwmaState>>> GetStatesAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
}
