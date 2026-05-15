using Domain.AI.Planner;
using Domain.AI.Sandbox;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Dispatches real-time notifications about plan execution progress.
/// Follows the fire-and-forget pattern used by existing notifiers
/// (<c>IDriftNotifier</c>, <c>IEscalationNotifier</c>). Implemented by
/// the AG-UI event bridge in the Presentation layer.
/// </summary>
public interface IPlanProgressNotifier
{
    /// <summary>Notifies that a plan has started executing.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="planName">Human-readable plan name.</param>
    /// <param name="graph">The full plan graph for client-side rendering.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyPlanStartedAsync(PlanId planId, string planName, PlanGraph graph, CancellationToken ct);

    /// <summary>Notifies that a step has started executing.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="stepId">Identifier of the step.</param>
    /// <param name="stepName">Human-readable step name.</param>
    /// <param name="type">The step's execution type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyStepStartedAsync(PlanId planId, PlanStepId stepId, string stepName, StepType type, CancellationToken ct);

    /// <summary>Notifies that a step has completed (successfully, failed, or skipped).</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="stepId">Identifier of the step.</param>
    /// <param name="status">Final status of the step.</param>
    /// <param name="duration">Wall-clock duration of the step execution.</param>
    /// <param name="outputSummary">Brief summary of step output. Null if no output.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyStepCompletedAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus status, TimeSpan duration, string? outputSummary, CancellationToken ct);

    /// <summary>Notifies that a step's status has changed.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="stepId">Identifier of the step.</param>
    /// <param name="previousStatus">The status before the transition.</param>
    /// <param name="newStatus">The status after the transition.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyStateUpdateAsync(PlanId planId, PlanStepId stepId, StepExecutionStatus previousStatus, StepExecutionStatus newStatus, CancellationToken ct);

    /// <summary>Notifies sandbox execution status for a tool step.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="stepId">Identifier of the step.</param>
    /// <param name="toolName">Name of the tool being executed.</param>
    /// <param name="isolationLevel">Sandbox isolation level used.</param>
    /// <param name="usage">Resource usage during execution.</param>
    /// <param name="attestationHash">HMAC attestation hash. Null if attestation unavailable.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifySandboxStatusAsync(PlanId planId, PlanStepId stepId, string toolName, SandboxIsolationLevel isolationLevel, ResourceUsage usage, string? attestationHash, CancellationToken ct);

    /// <summary>Notifies that the entire plan completed successfully.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="totalDuration">Total wall-clock duration of the plan execution.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyPlanCompletedAsync(PlanId planId, TimeSpan totalDuration, CancellationToken ct);

    /// <summary>Notifies that the plan failed due to a step failure.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="failedStepId">Identifier of the step that caused the failure.</param>
    /// <param name="errorMessage">Error message from the failed step.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyPlanFailedAsync(PlanId planId, PlanStepId failedStepId, string errorMessage, CancellationToken ct);
}
