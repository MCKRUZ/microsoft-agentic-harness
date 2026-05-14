using Domain.AI.DriftDetection;
using Domain.Common;

namespace Application.AI.Common.Interfaces.DriftDetection;

/// <summary>
/// Core service for evaluating agent quality drift against baselines.
/// Orchestrates scoring, notification, and audit trail creation.
/// </summary>
public interface IDriftDetectionService
{
    /// <summary>Evaluates current dimension scores against the active baseline.</summary>
    Task<Result<DriftScore>> EvaluateDriftAsync(DriftEvaluationRequest request, CancellationToken ct);

    /// <summary>Retrieves the active baseline for a scope. Returns null value when no baseline exists.</summary>
    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);

    /// <summary>Recalculates and persists a baseline from recent evaluation history.</summary>
    Task<Result<DriftBaseline>> UpdateBaselineAsync(DriftBaselineUpdateRequest request, CancellationToken ct);

    /// <summary>Retrieves historical drift scores within a time window.</summary>
    Task<Result<IReadOnlyList<DriftScore>>> GetDriftHistoryAsync(DriftHistoryQuery query, CancellationToken ct);
}
