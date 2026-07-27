using Application.AI.Common.Services.Governance;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Capability-envelope confinement at the scheduler level: the autonomy ceiling that gates every
/// step type before its executor is resolved, and the terminal failure path shared by every
/// governance denial in the engine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why denials are terminal.</strong> <see cref="RetryPolicy"/> — including
/// <see cref="RetryPolicy.OnExhausted"/> — is plan-authored data. Routing a policy denial through
/// the ordinary failure path would let a plan declare <see cref="ErrorRecovery.SkipStep"/> on the
/// very step the envelope blocks: recovery marks it Skipped, the summary sees no Failed state, and
/// the run reports Completed while a security denial was silently dropped. An
/// <see cref="ErrorRecovery.Escalate"/> policy fails differently but no better — approve → re-run →
/// deny → escalate again, asking a human to approve something the envelope will never permit.
/// Denials therefore bypass <c>HandleStepFailureAsync</c> entirely, exactly as
/// <c>FailRejectedBlockAsync</c> already does for a rejected escalation and for the same reason.
/// </para>
/// <para>
/// With no ambient envelope (every direct in-process <see cref="IPlanExecutor"/> caller) no ceiling
/// applies and this partial contributes nothing to execution.
/// </para>
/// </remarks>
public sealed partial class PlanExecutor
{
    /// <summary>
    /// Denies the step when it declares a <see cref="PlanStep.RequiredAutonomyLevel"/> above the
    /// ambient capability envelope's ceiling, failing it terminally.
    /// </summary>
    /// <returns><c>true</c> when the step was denied and must not run; <c>false</c> to proceed.</returns>
    private async Task<bool> TryDenyForAutonomyCeilingAsync(
        PlanStep step, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        var violation = DescribeAutonomyCeilingViolation(step);
        if (violation is null)
            return false;

        _logger.LogWarning(
            "Step {StepId} in plan {PlanId} denied by envelope autonomy ceiling: {Reason}",
            step.Id, ctx.PlanId, violation);

        await _notifier.NotifyStepStartedAsync(ctx.PlanId, step.Id, step.Name, step.Type, ct);
        await FailPolicyDeniedStepAsync(step, violation, ctx, ct);
        return true;
    }

    /// <summary>
    /// Describes why the step violates the ambient capability envelope's autonomy ceiling, or null
    /// when it may run: no envelope armed, no <see cref="PlanStep.RequiredAutonomyLevel"/> declared,
    /// or the required tier is within the ceiling. The comparison relies on the numeric ordering of
    /// <see cref="Domain.AI.Governance.AutonomyLevel"/> (higher value = more trust required).
    /// </summary>
    /// <remarks>
    /// This covers only steps that <em>declare</em> a required tier. A step that declares nothing is
    /// not exempt from the envelope — its per-operation authorization (tool, retrieval, or inference)
    /// runs through <c>IToolInvocationGovernor</c>, where the envelope's own autonomy-ceiling baseline
    /// applies. The two checks are complementary: this one is a static plan-shape assertion, that one
    /// is the live per-operation gate.
    /// </remarks>
    private static string? DescribeAutonomyCeilingViolation(PlanStep step)
    {
        var envelope = CapabilityEnvelopeAccessor.Current;
        if (envelope is null || step.RequiredAutonomyLevel is not { } required)
            return null;

        return required <= envelope.AutonomyCeiling
            ? null
            : $"Step '{step.Name}' requires autonomy level {required} but the capability envelope " +
              $"permits at most {envelope.AutonomyCeiling}.";
    }

    /// <summary>
    /// Fails a step whose operation was refused by governance and skips its downstream subgraph.
    /// Deliberately does NOT route through <c>HandleStepFailureAsync</c> — see the type remarks.
    /// </summary>
    /// <param name="step">The denied step.</param>
    /// <param name="denialMessage">The caller-facing denial text to persist as the step error.</param>
    /// <param name="ctx">The running plan's scheduling state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="result">
    /// The executor's result when the denial came from a step executor, so the step-completed
    /// notification carries its real duration. Null for the scheduler's own ceiling denial, which
    /// never reached an executor.
    /// </param>
    private async Task FailPolicyDeniedStepAsync(
        PlanStep step,
        string? denialMessage,
        PlanExecutionRuntime ctx,
        CancellationToken ct,
        StepExecutionResult? result = null)
    {
        await TransitionStepAsync(
            ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: denialMessage);

        // Downstream is skipped, never enqueued: a step the envelope refused produced no output, so
        // anything depending on it cannot legitimately run.
        await SkipDownstreamSubgraphAsync(step.Id, ctx);

        if (result is not null)
        {
            await _notifier.NotifyStepCompletedAsync(
                ctx.PlanId, step.Id, StepExecutionStatus.Failed, result.Duration, result.Output, ct);
        }
    }
}
