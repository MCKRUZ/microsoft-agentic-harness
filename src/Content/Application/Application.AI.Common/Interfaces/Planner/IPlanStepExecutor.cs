using Domain.AI.Planner;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Executes a single <see cref="PlanStep"/> within a plan. Implementations are registered
/// via keyed dependency injection on <see cref="StepType"/>, enabling each step type
/// to have a specialized executor.
/// </summary>
public interface IPlanStepExecutor
{
    /// <summary>
    /// Executes the given step using outputs from upstream dependencies as context.
    /// </summary>
    /// <param name="step">The step to execute.</param>
    /// <param name="upstreamOutputs">
    /// Outputs from completed upstream steps, keyed by step ID. Provides data flow
    /// between connected steps in the plan graph.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result including status, output, and optional attestation.</returns>
    Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct);
}
