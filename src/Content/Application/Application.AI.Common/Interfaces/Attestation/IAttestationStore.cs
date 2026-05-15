using Domain.AI.Attestation;
using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Attestation;

/// <summary>
/// Persists and retrieves <see cref="ToolExecutionAttestation"/> records
/// for audit trail and verification purposes.
/// </summary>
public interface IAttestationStore
{
    /// <summary>Saves an attestation linked to a specific plan step.</summary>
    /// <param name="stepId">Identifier of the step that generated the attestation.</param>
    /// <param name="attestation">The attestation to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> SaveAsync(PlanStepId stepId, ToolExecutionAttestation attestation, CancellationToken ct);

    /// <summary>Retrieves the attestation for a specific step. Returns null inside the result if not found.</summary>
    /// <param name="stepId">Identifier of the step.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<ToolExecutionAttestation?>> GetByStepAsync(PlanStepId stepId, CancellationToken ct);

    /// <summary>Retrieves all attestations for a plan's steps.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<ToolExecutionAttestation>>> GetByPlanAsync(PlanId planId, CancellationToken ct);
}
