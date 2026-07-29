using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.AI.Sandbox;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Republishes plan execution notifications as run progress, so a caller watching a run sees the
/// workflow it started actually moving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a bridge rather than publishing from the executor.</strong> The planner already
/// announces everything worth watching through <see cref="IPlanProgressNotifier"/>, and it announces
/// it about a <em>plan</em>. A run is a different thing: the same stored workflow can be run many
/// times over its life. Translating at the boundary keeps the planner unaware that runs exist, and
/// keeps the run substrate unaware of plans.
/// </para>
/// <para>
/// <strong>The translation is only possible because a workflow has at most one live run.</strong>
/// Notifications identify a plan, and a stored workflow's id <em>is</em> its plan id — so while a run
/// is live, plan and run are in one-to-one correspondence and progress can be attributed. Were two
/// runs of one workflow ever permitted, this would have no correct answer, and the honest failure
/// would be to publish nothing rather than guess.
/// </para>
/// <para>
/// <strong>Nothing here fails a run.</strong> A plan that runs perfectly while nobody is watching has
/// not gone wrong, so an unattributable notification is simply dropped. Progress reporting is not
/// permitted to be a reason work fails.
/// </para>
/// </remarks>
public sealed class PlanProgressRunBridge : IPlanProgressNotifier
{
    private readonly IRunJobStore _runStore;
    private readonly IRunProgressBroker _broker;

    /// <summary>Initializes the bridge.</summary>
    public PlanProgressRunBridge(IRunJobStore runStore, IRunProgressBroker broker)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(broker);

        _runStore = runStore;
        _broker = broker;
    }

    /// <inheritdoc />
    public Task NotifyPlanStartedAsync(PlanId planId, string planName, PlanGraph graph, CancellationToken ct)
    {
        Publish(planId, RunProgressKind.RunStarted, status: nameof(RunStatus.Running), detail: planName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyStepStartedAsync(
        PlanId planId, PlanStepId stepId, string stepName, StepType type, CancellationToken ct)
    {
        Publish(
            planId,
            RunProgressKind.StepStarted,
            stepId: stepId.Value.ToString(),
            stepName: stepName,
            status: nameof(StepExecutionStatus.Running),
            detail: type.ToString());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyStepCompletedAsync(
        PlanId planId,
        PlanStepId stepId,
        StepExecutionStatus status,
        TimeSpan duration,
        string? outputSummary,
        CancellationToken ct)
    {
        // The output summary is deliberately not forwarded. A progress feed says how far along the
        // work is; what the work produced belongs to the result, which has its own authorization.
        Publish(
            planId,
            RunProgressKind.StepCompleted,
            stepId: stepId.Value.ToString(),
            status: status.ToString());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyPlanCompletedAsync(PlanId planId, TimeSpan totalDuration, CancellationToken ct)
    {
        // Deliberately silent. The run's terminal event is published by the dispatcher, which is the
        // one place every run passes through on its way to ending — the planner only announces the
        // endings it produces, and a run can end without it having run at all.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyPlanFailedAsync(
        PlanId planId, PlanStepId failedStepId, string errorMessage, CancellationToken ct)
    {
        // Silent for the same reason as completion. The failing step already reached the watcher as a
        // step event, and the run's ending — with its caller-safe reason — is the dispatcher's to
        // report, so that every way a run can end is announced by the same code.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyStateUpdateAsync(
        PlanId planId,
        PlanStepId stepId,
        StepExecutionStatus previousStatus,
        StepExecutionStatus newStatus,
        CancellationToken ct)
    {
        // Not republished. Every transition a watcher can act on already arrives as a step start or a
        // step completion; forwarding the raw transitions as well would double the feed's volume to
        // say the same things twice, and the buffer that pays for it is the watcher's.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifySandboxStatusAsync(
        PlanId planId,
        PlanStepId stepId,
        string toolName,
        SandboxIsolationLevel isolationLevel,
        ResourceUsage usage,
        string? attestationHash,
        CancellationToken ct)
    {
        // Not republished. Isolation levels, resource usage and attestation hashes describe how the
        // host executed something, not how far along the caller's work is — and they are the kind of
        // internal detail a caller-facing feed should not carry.
        return Task.CompletedTask;
    }

    private void Publish(
        PlanId planId,
        RunProgressKind kind,
        string? stepId = null,
        string? stepName = null,
        string? status = null,
        string? detail = null)
    {
        var run = _runStore.FindLiveRunForTarget(RunKind.Workflow, planId.Value.ToString());
        if (run is null)
            return;

        _broker.Publish(run.JobId, kind, stepId, stepName, status, detail);
    }
}
