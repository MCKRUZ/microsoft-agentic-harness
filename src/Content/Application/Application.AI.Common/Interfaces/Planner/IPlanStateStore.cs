using Domain.AI.Planner;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Persists and retrieves plan graphs, step execution states, and execution history.
/// Supports checkpoint/resume for fault-tolerant plan execution and optimistic
/// concurrency for concurrent step updates.
/// </summary>
public interface IPlanStateStore
{
    /// <summary>Persists a new plan graph.</summary>
    /// <param name="plan">The plan to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> SavePlanAsync(PlanGraph plan, CancellationToken ct);

    /// <summary>Loads a plan by its identifier. Returns null inside the result if not found.</summary>
    /// <param name="planId">Identifier of the plan to load.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<PlanGraph?>> LoadPlanAsync(PlanId planId, CancellationToken ct);

    /// <summary>Updates the execution state of a single step with optimistic concurrency.</summary>
    /// <param name="state">The updated step execution state.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> UpdateStepStateAsync(StepExecutionState state, CancellationToken ct);

    /// <summary>
    /// Saves a checkpoint of all step states for fault-tolerant resume.
    /// </summary>
    /// <param name="planId">Identifier of the plan to checkpoint.</param>
    /// <param name="states">Current states of all steps.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> CheckpointAsync(PlanId planId, IReadOnlyList<StepExecutionState> states, CancellationToken ct);

    /// <summary>
    /// Resumes a plan from the last checkpoint, returning per-step states for ready-queue rebuild.
    /// </summary>
    /// <param name="planId">Identifier of the plan to resume.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>> ResumeAsync(PlanId planId, CancellationToken ct);

    /// <summary>
    /// Loads the current step execution states for a plan without mutation.
    /// Unlike <see cref="ResumeAsync"/>, this does not reset Running steps to Ready.
    /// </summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>> LoadStepStatesAsync(
        PlanId planId, CancellationToken ct);

    /// <summary>Retrieves the execution history for a plan.</summary>
    /// <param name="planId">Identifier of the plan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PlanExecutionLogEntry>>> GetExecutionHistoryAsync(PlanId planId, CancellationToken ct);

    /// <summary>
    /// Lists plans with optional filtering by status and time range.
    /// </summary>
    /// <param name="statusFilter">Optional status filter.</param>
    /// <param name="from">Optional start of time range.</param>
    /// <param name="to">Optional end of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IReadOnlyList<PlanGraph>>> ListPlansAsync(
        StepExecutionStatus? statusFilter = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);
}
