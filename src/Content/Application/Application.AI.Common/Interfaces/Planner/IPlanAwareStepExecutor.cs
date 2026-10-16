using Domain.AI.Planner;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// A step executor that needs to know which plan it is running inside, not only which step.
/// </summary>
/// <remarks>
/// <para>
/// The plan executor calls the overload that carries the plan id whenever the executor implements this
/// interface, and the plain <see cref="IPlanStepExecutor.ExecuteAsync"/> otherwise. The plan id is
/// passed explicitly because the scoped <see cref="PlanExecutionContext"/> executors are constructed
/// with is not populated for the running plan, so an executor that read its plan id from there would
/// see nothing for a root plan.
/// </para>
/// <para>
/// Today this is the human gate, which stamps the plan and step on the escalation it queues so that
/// only that escalation's verdict can release that step (see <c>EscalationBinding</c>).
/// </para>
/// </remarks>
public interface IPlanAwareStepExecutor : IPlanStepExecutor
{
    /// <summary>
    /// Executes <paramref name="step"/> as part of the plan <paramref name="planId"/>.
    /// </summary>
    /// <param name="planId">The plan the step belongs to.</param>
    /// <param name="step">The step to execute.</param>
    /// <param name="upstreamOutputs">Outputs from completed upstream steps, keyed by step ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result including status, output, and optional attestation.</returns>
    Task<StepExecutionResult> ExecuteAsync(
        PlanId planId,
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct);
}
