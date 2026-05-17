using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Executes a validated <see cref="PlanGraph"/> by scheduling steps according to
/// the graph's dependency structure, handling retries, and checkpointing progress.
/// Supports both fresh execution and resumption from a previous checkpoint.
/// </summary>
public interface IPlanExecutor
{
    /// <summary>
    /// Executes the plan identified by <paramref name="planId"/>. If a checkpoint
    /// exists, resumes from the last saved state.
    /// </summary>
    /// <param name="planId">Identifier of the plan to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution summary on success, or a failure result.</returns>
    Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct);

    /// <summary>
    /// Executes a child plan with explicit execution context (for sub-plan depth tracking).
    /// </summary>
    /// <param name="planId">Identifier of the plan to execute.</param>
    /// <param name="context">Execution context with incremented depth for recursion enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution summary on success, or a failure result.</returns>
    Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, PlanExecutionContext context, CancellationToken ct);

    /// <summary>
    /// Cancels a running plan execution. Steps already in progress complete but no new steps start.
    /// </summary>
    /// <param name="planId">Identifier of the plan to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> CancelAsync(PlanId planId, CancellationToken ct);

    /// <summary>
    /// Retries a specific failed step within a plan. The plan must be in a failed or partially-complete state.
    /// </summary>
    /// <param name="planId">Identifier of the plan containing the step.</param>
    /// <param name="stepId">Identifier of the step to retry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct);
}
