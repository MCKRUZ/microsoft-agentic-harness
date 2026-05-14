using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Persistence contract for drift baselines.
/// Keyed DI: <c>"graph"</c> (default), <c>"in_memory"</c> (testing).
/// </summary>
public interface IDriftBaselineStore
{
    /// <summary>Persists a baseline snapshot, overwriting any previous baseline for the same scope+identifier.</summary>
    Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct);

    /// <summary>Retrieves the active baseline for a scope. Returns null value when none exists.</summary>
    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);

    /// <summary>Lists all baselines, optionally filtered by scope.</summary>
    Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(DriftScope? scope, CancellationToken ct);
}
