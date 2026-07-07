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
    /// <remarks>
    /// <para>
    /// <b>At-least-once execution on resume.</b> Crash-resume re-runs any step that was in-flight
    /// when the host stopped: a step persisted as <see cref="StepExecutionStatus.Running"/> is
    /// renormalised to <see cref="StepExecutionStatus.Pending"/> and executed again — even if it had
    /// actually finished its side effects before the crash but died before persisting
    /// <see cref="StepExecutionStatus.Completed"/>. Manual retry via
    /// <c>IPlanExecutor.RetryStepAsync</c> has the same effect.
    /// </para>
    /// <para>
    /// Implementations of non-idempotent step types (for example <see cref="StepType.ToolUse"/> or
    /// <see cref="StepType.SubPlanInvocation"/>) MUST therefore tolerate being invoked more than once
    /// for the same step — guard external writes with idempotency keys, dedup checks, or compensating
    /// logic. The harness guarantees at-least-once, not exactly-once, execution.
    /// </para>
    /// </remarks>
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
