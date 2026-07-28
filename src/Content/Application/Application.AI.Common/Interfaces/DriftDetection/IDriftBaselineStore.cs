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

    /// <summary>
    /// Retrieves the baseline snapshot carrying the given <see cref="DriftBaseline.BaselineId"/>,
    /// or a null value when no active baseline has that id. Returns null (never a failure) for an
    /// unknown id, so callers can map absence to their own not-found semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Baselines are keyed by scope + identifier, not by id, so this is a secondary lookup and
    /// implementations may still have to scan candidates. What it lets a backend avoid is
    /// <em>materializing</em> them: the graph implementation filters on the stored
    /// <c>BaselineId</c> property and deserializes at most one node's JSON payload, instead of
    /// reconstructing every <see cref="DriftBaseline"/> in the store to find one. An
    /// implementation with a real secondary index should use it.
    /// </para>
    /// </remarks>
    /// <param name="baselineId">The baseline snapshot id to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<DriftBaseline?>> GetBaselineByIdAsync(Guid baselineId, CancellationToken ct);
}
