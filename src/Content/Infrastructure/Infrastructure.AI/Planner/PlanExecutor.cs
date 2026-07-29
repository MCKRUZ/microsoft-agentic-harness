using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Core DAG scheduling engine that drives plan execution. Implements dynamic ready-queue
/// scheduling with bounded concurrency, checkpoint/resume, escalation-gated step reconciliation on
/// resume, conditional branching, and per-plan serialization.
/// </summary>
/// <remarks>
/// A step that reaches <see cref="Domain.AI.Planner.StepExecutionStatus.Blocked"/> (a human gate, or
/// an escalate-on-failure step) is not polled in-loop. Instead the escalation identifier is persisted
/// with the step, the scheduler drains when only blocked steps remain, and the block is resolved on
/// the next call to <see cref="ExecuteAsync(Domain.AI.Planner.PlanId, System.Threading.CancellationToken)"/>
/// via <c>ReconcileBlockedStepsAsync</c>: an approved escalation completes the step and continues the
/// plan, a rejected one fails it through recovery.
/// </remarks>
public sealed partial class PlanExecutor : IPlanExecutor
{
    private static readonly ActivitySource ActivitySource = new("PlanExecution");
    private static readonly Meter Meter = new("PlanExecution");
    private static readonly Counter<long> PlanExecutionsCounter = Meter.CreateCounter<long>("planner.plan.executions");
    private static readonly Counter<long> StepExecutionsCounter = Meter.CreateCounter<long>("planner.step.executions");
    private static readonly Histogram<double> StepDurationHistogram = Meter.CreateHistogram<double>("planner.step.duration", "ms");

    private static readonly Dictionary<PlanId, RefCountedLock> PlanLocks = new();
    private static readonly object PlanLocksGate = new();

    private readonly IPlanValidator _validator;
    private readonly IPlanStateStore _stateStore;
    private readonly IPlanProgressNotifier _notifier;
    private readonly IEscalationService _escalationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPlanRunCancellationRegistry _cancellationRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlanExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlanExecutor"/> class.
    /// </summary>
    /// <param name="validator">Validates plan structure before execution.</param>
    /// <param name="stateStore">Persists plan and step execution state for checkpoint/resume.</param>
    /// <param name="notifier">Receives plan and step lifecycle notifications.</param>
    /// <param name="escalationService">Resolves human-gate and escalate-on-failure outcomes.</param>
    /// <param name="serviceProvider">Resolves keyed step executors by <see cref="StepType"/>.</param>
    /// <param name="cancellationRegistry">
    /// Process-wide index of in-flight runs. <see cref="ExecuteAsync(PlanId, PlanExecutionContext, CancellationToken)"/>
    /// registers each run here and <see cref="CancelAsync"/> signals it, which is what makes a
    /// cancel stop work rather than only rewrite state after the run ends.
    /// </param>
    /// <param name="timeProvider">Clock used for retry backoff delays; injectable for tests.</param>
    /// <param name="logger">Structured logger for execution auditing.</param>
    public PlanExecutor(
        IPlanValidator validator,
        IPlanStateStore stateStore,
        IPlanProgressNotifier notifier,
        IEscalationService escalationService,
        IServiceProvider serviceProvider,
        IPlanRunCancellationRegistry cancellationRegistry,
        TimeProvider timeProvider,
        ILogger<PlanExecutor> logger)
    {
        _validator = validator;
        _stateStore = stateStore;
        _notifier = notifier;
        _escalationService = escalationService;
        _serviceProvider = serviceProvider;
        _cancellationRegistry = cancellationRegistry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct)
        => ExecuteAsync(planId, new PlanExecutionContext(), ct);

    public async Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, PlanExecutionContext context, CancellationToken ct)
    {
        if (context.Depth >= context.MaxDepth)
            return Result<PlanExecutionSummary>.Fail($"Maximum sub-plan depth {context.MaxDepth} exceeded at depth {context.Depth}.");

        // Registration is taken BEFORE the plan lock so a run still queued behind an earlier run is
        // cancellable too — it then acquires the lock with an already-signalled token and unwinds
        // immediately instead of starting work someone has asked to stop. `using` makes release
        // symmetric with registration by construction: the component that registers is the only one
        // that can release, and it does so on every exit path.
        using var registration = _cancellationRegistry.Register(planId);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, registration.Token);

        var planLock = AcquirePlanLockHandle(planId);
        try
        {
            await planLock.Semaphore.WaitAsync(ct);
        }
        catch
        {
            ReleasePlanLockHandle(planId, planLock);
            throw;
        }

        try
        {
            return await ExecuteCoreAsync(planId, context, runCts.Token, registration.Token);
        }
        catch (OperationCanceledException) when (registration.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // A registry cancel that landed outside the scheduling loop (during load, validation,
            // blocked-step reconciliation, or the initial enqueue). Inside the loop the existing
            // handler already unwinds to a summary. Returning a failure Result rather than throwing
            // keeps ExecuteAsync total for cancellation: a cancel is an expected outcome, so callers
            // — including a run API — read it from the Result instead of catching.
            _logger.LogInformation("Plan {PlanId} run cancelled before the scheduling loop completed", planId);
            return Result<PlanExecutionSummary>.Fail($"Plan {planId} execution was cancelled.");
        }
        finally
        {
            ReleasePlanLock(planId, planLock);
        }
    }

    /// <summary>
    /// Cancels a plan. Signals any in-flight run first, then rewrites persisted state for every
    /// step that is not already terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The signal must come first, and must not take the plan lock.</b> A run holds the per-plan
    /// lock for its entire duration, so a cancel that acquired the lock before signalling would
    /// block until the run it is trying to stop had finished naturally, and would then "cancel"
    /// work that had already completed — a cancel that silently does nothing. The registry pre-pass
    /// below is non-blocking: it signals the run's token and returns, and the run releases the lock
    /// as it unwinds, which is what lets the state rewrite that follows describe a genuinely
    /// stopped plan.
    /// </para>
    /// <para>
    /// <b>Side effects are at-least-once.</b> Cancelling signals the token that the in-flight step
    /// is executing under; it does not roll anything back. A tool call, LLM request, or sub-plan
    /// that had already started may complete, and any external effect it had — a file written, a
    /// message sent, a row inserted — stands. Cancellation stops further work; it does not undo
    /// work already done.
    /// </para>
    /// <para>
    /// <b>The plan stays resumable.</b> Steps that finished keep their <c>Completed</c> state and
    /// their output; everything else is recorded as <see cref="StepExecutionStatus.Cancelled"/>.
    /// A later <see cref="ExecuteAsync(PlanId, CancellationToken)"/> resumes from that checkpoint —
    /// <c>InitializeStepStates</c> renormalises <c>Cancelled</c> back to <c>Pending</c>, so the plan
    /// picks up where it stopped rather than re-running completed work.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> A second cancel signals an already-signalled token (or finds no live run)
    /// and rewrites state that is already terminal, so it is a no-op that still reports success.
    /// </para>
    /// </remarks>
    /// <param name="planId">Identifier of the plan to cancel.</param>
    /// <param name="ct">Cancellation token for the cancel request itself.</param>
    public async Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
    {
        _logger.LogInformation("Plan {PlanId} cancellation requested", planId);

        var signalled = _cancellationRegistry.TryCancel(planId);
        _logger.LogInformation(
            signalled
                ? "Plan {PlanId} had an in-flight run; cancellation signalled"
                : "Plan {PlanId} had no in-flight run to signal; rewriting persisted state only",
            planId);

        var planLock = AcquirePlanLockHandle(planId);
        try
        {
            await planLock.Semaphore.WaitAsync(ct);
        }
        catch
        {
            ReleasePlanLockHandle(planId, planLock);
            throw;
        }

        try
        {
            var loadResult = await _stateStore.LoadStepStatesAsync(planId, ct);
            if (!loadResult.IsSuccess)
                return Result.Fail(loadResult.Errors.ToArray());

            var stepStates = loadResult.Value;
            if (stepStates is null || stepStates.Count == 0)
                return Result.NotFound($"No step states found for plan {planId}.");

            var updatedStates = new List<StepExecutionState>();
            foreach (var (stepId, state) in stepStates)
            {
                if (state.Status is StepExecutionStatus.Completed
                    or StepExecutionStatus.Failed
                    or StepExecutionStatus.Cancelled
                    or StepExecutionStatus.Skipped)
                {
                    updatedStates.Add(state);
                    continue;
                }

                updatedStates.Add(state with
                {
                    Status = StepExecutionStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Plan cancelled by user"
                });
            }

            var checkpointResult = await _stateStore.CheckpointAsync(planId, updatedStates, ct);
            if (!checkpointResult.IsSuccess)
                return Result.Fail(checkpointResult.Errors.ToArray());

            var cancelledCount = updatedStates.Count(s => s.Status == StepExecutionStatus.Cancelled);
            _logger.LogInformation(
                "Plan {PlanId} cancelled: {CancelledCount} steps cancelled, {TerminalCount} already terminal",
                planId, cancelledCount, updatedStates.Count - cancelledCount);

            return Result.Success();
        }
        finally
        {
            ReleasePlanLock(planId, planLock);
        }
    }

    /// <summary>
    /// Operator-initiated retry of a failed step: resets it to <see cref="StepExecutionStatus.Pending"/>
    /// so the next <see cref="ExecuteAsync(PlanId, CancellationToken)"/> re-runs it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately unbounded.</b> This method does NOT consult
    /// <see cref="RetryPolicy.MaxRetries"/> — that budget governs only the executor's automatic
    /// in-run retries. A human operator explicitly re-arming a failed step is an intentional
    /// override, so the number of manual retries is not capped.
    /// </para>
    /// <para>
    /// The step's <see cref="StepExecutionState.AttemptCount"/> is preserved across the reset, so
    /// the automatic retry budget stays spent: the re-run executes once and, if it fails again,
    /// goes straight back through <see cref="RetryPolicy.OnExhausted"/> rather than restarting
    /// the backoff ladder. Each execution remains subject to at-least-once semantics — see
    /// <see cref="IPlanStepExecutor.ExecuteAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
    {
        _logger.LogInformation("Retry requested for step {StepId} in plan {PlanId}", stepId, planId);

        var planLock = AcquirePlanLockHandle(planId);
        try
        {
            await planLock.Semaphore.WaitAsync(ct);
        }
        catch
        {
            ReleasePlanLockHandle(planId, planLock);
            throw;
        }

        try
        {
            var loadResult = await _stateStore.LoadStepStatesAsync(planId, ct);
            if (!loadResult.IsSuccess)
                return Result.Fail(loadResult.Errors.ToArray());

            var stepStates = loadResult.Value;
            if (stepStates is null || stepStates.Count == 0)
                return Result.NotFound($"No step states found for plan {planId}.");

            if (!stepStates.TryGetValue(stepId, out var currentState))
                return Result.NotFound($"Step {stepId} not found in plan {planId}.");

            if (currentState.Status != StepExecutionStatus.Failed)
                return Result.Fail($"Only failed steps can be retried. Step {stepId} is {currentState.Status}.");

            // Reset to Pending — not Ready — so the next ExecuteAsync re-evaluates the step's
            // dependencies and re-enqueues it through the canonical Pending -> Ready promotion path.
            // A persisted Ready step is never picked up by EnqueueInitialReadyStepsAsync, so the
            // retried step would never actually re-run.
            var resetState = new StepExecutionState
            {
                StepId = stepId,
                Status = StepExecutionStatus.Pending,
                AttemptCount = currentState.AttemptCount,
                StartedAt = null,
                CompletedAt = null,
                Output = null,
                ErrorMessage = null
            };

            var updateResult = await _stateStore.UpdateStepStateAsync(resetState, ct);
            if (!updateResult.IsSuccess)
                return Result.Fail(updateResult.Errors.ToArray());

            _logger.LogInformation(
                "Step {StepId} in plan {PlanId} reset to Pending for retry (attempt {AttemptCount} total)",
                stepId, planId, currentState.AttemptCount);

            return Result.Success();
        }
        finally
        {
            ReleasePlanLock(planId, planLock);
        }
    }

    /// <summary>
    /// Atomically obtains (or creates) the per-plan lock holder and registers this caller as a
    /// holder by incrementing its reference count under <see cref="PlanLocksGate"/>. The returned
    /// holder is guaranteed not to be disposed until the matching <see cref="ReleasePlanLock"/>
    /// runs, which closes the check-remove-dispose TOCTOU window that a bare
    /// <c>ConcurrentDictionary.GetOrAdd</c> + <c>SemaphoreSlim.Dispose</c> pattern exposes.
    /// </summary>
    private static RefCountedLock AcquirePlanLockHandle(PlanId planId)
    {
        lock (PlanLocksGate)
        {
            if (!PlanLocks.TryGetValue(planId, out var holder))
            {
                holder = new RefCountedLock();
                PlanLocks[planId] = holder;
            }

            holder.RefCount++;
            return holder;
        }
    }

    /// <summary>
    /// Releases the per-plan semaphore and decrements the holder's reference count under
    /// <see cref="PlanLocksGate"/>. The holder is removed from the dictionary and disposed only
    /// when the last holder releases it, so a concurrent <see cref="AcquirePlanLockHandle"/> can
    /// never observe a half-disposed semaphore.
    /// </summary>
    private static void ReleasePlanLock(PlanId planId, RefCountedLock planLock)
    {
        planLock.Semaphore.Release();
        ReleasePlanLockHandle(planId, planLock);
    }

    /// <summary>
    /// Drops this caller's reference to the holder without releasing the semaphore. Used on the
    /// acquisition-failure path (e.g. <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// cancelled before the lock was taken) so the reference count is not leaked, which would
    /// otherwise permanently pin the dictionary entry and prevent disposal.
    /// </summary>
    private static void ReleasePlanLockHandle(PlanId planId, RefCountedLock planLock)
    {
        lock (PlanLocksGate)
        {
            planLock.RefCount--;
            if (planLock.RefCount == 0)
            {
                PlanLocks.Remove(planId);
                planLock.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// A reference-counted wrapper around the per-plan <see cref="SemaphoreSlim"/>. The reference
    /// count tracks how many callers currently hold or are waiting on the semaphore; the semaphore
    /// is disposed exactly once, when the count returns to zero. All mutation of
    /// <see cref="RefCount"/> and the owning dictionary happens under <see cref="PlanLocksGate"/>,
    /// so lifetime transitions are atomic with acquisition.
    /// </summary>
    private sealed class RefCountedLock
    {
        /// <summary>The binary semaphore providing per-plan mutual exclusion.</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>
        /// Number of callers that have acquired this holder and not yet released it. Guarded by
        /// <see cref="PlanLocksGate"/>; never mutated outside the lock.
        /// </summary>
        public int RefCount { get; set; }
    }

    /// <param name="runCancellationToken">
    /// The registry token alone, without the caller's token folded in. <paramref name="ct"/> is the
    /// union of the two and drives the work; this one only answers "was this an operator cancel?",
    /// which decides whether an interrupted step is recorded as Cancelled (resumable) or Failed.
    /// </param>
    private async Task<Result<PlanExecutionSummary>> ExecuteCoreAsync(
        PlanId planId,
        PlanExecutionContext context,
        CancellationToken ct,
        CancellationToken runCancellationToken)
    {
        using var activity = ActivitySource.StartActivity("plan.execute");
        activity?.SetTag("plan.id", planId.Value.ToString());

        var loadResult = await _stateStore.LoadPlanAsync(planId, ct);
        if (!loadResult.IsSuccess)
            return Result<PlanExecutionSummary>.Fail(loadResult.Errors.ToArray());

        var plan = loadResult.Value;
        if (plan is null)
            return Result<PlanExecutionSummary>.Fail("Plan not found.");

        activity?.SetTag("plan.name", plan.Name);
        activity?.SetTag("plan.step_count", plan.Steps.Count);

        var validationResult = await _validator.ValidateAsync(plan, ct);
        if (!validationResult.IsSuccess)
            return Result<PlanExecutionSummary>.Fail(validationResult.Errors.ToArray());

        if (!validationResult.Value!.IsValid)
            return Result<PlanExecutionSummary>.Fail(validationResult.Value.Errors.ToArray());

        if (plan.Steps.Count == 0)
        {
            await _notifier.NotifyPlanStartedAsync(planId, plan.Name, plan, ct);
            await _notifier.NotifyPlanCompletedAsync(planId, TimeSpan.Zero, ct);
            PlanExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "completed"));
            return Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = planId,
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            });
        }

        var resumeResult = await _stateStore.ResumeAsync(planId, ct);
        var existingStates = resumeResult.IsSuccess && resumeResult.Value!.Count > 0
            ? resumeResult.Value
            : null;

        var stepStates = new ConcurrentDictionary<PlanStepId, StepExecutionState>();
        InitializeStepStates(plan, stepStates, existingStates);

        await _notifier.NotifyPlanStartedAsync(planId, plan.Name, plan, ct);

        var sw = Stopwatch.StartNew();
        using var planCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        planCts.CancelAfter(plan.Configuration.PlanTimeout);

        var (dependencyMap, dependentMap) = BuildGraphMaps(plan);
        var stepLookup = plan.Steps.ToDictionary(s => s.Id);
        var stepOutputs = new ConcurrentDictionary<PlanStepId, string>();

        if (existingStates is not null)
        {
            foreach (var (stepId, state) in existingStates)
            {
                if (state.Status == StepExecutionStatus.Completed && state.Output is not null)
                    stepOutputs[stepId] = state.Output;
            }
        }

        var readyQueue = new ConcurrentQueue<PlanStep>();
        using var concurrency = new SemaphoreSlim(plan.Configuration.MaxParallelSteps, plan.Configuration.MaxParallelSteps);
        var runningTasks = new HashSet<Task>();

        var execCtx = new PlanExecutionRuntime(
            planId, stepStates, stepOutputs, dependencyMap, dependentMap, stepLookup, readyQueue, concurrency,
            runCancellationToken);

        // Resolve any steps parked in Blocked whose escalation has since been decided. Approved gates
        // complete and release their downstream (which this may enqueue), rejected ones fail through
        // recovery. Runs before the initial enqueue so freshly-released downstream steps are scheduled
        // in this same execution.
        await ReconcileBlockedStepsAsync(plan, execCtx, planCts.Token);
        await EnqueueInitialReadyStepsAsync(plan, stepStates, dependencyMap, readyQueue, planId, planCts.Token);

        try
        {
            await RunSchedulingLoopAsync(execCtx, runningTasks, planCts.Token);
        }
        catch (OperationCanceledException) when (planCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Plan {PlanId} timed out after {Timeout}", planId, plan.Configuration.PlanTimeout);
            try { await Task.WhenAll(runningTasks); } catch (OperationCanceledException) { }
            MarkRemainingAs(stepStates, StepExecutionStatus.Failed, "Plan timeout exceeded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An operator cancel is a pause, not a failure: its steps are recorded Cancelled so the
            // plan resumes from this checkpoint. A caller-token cancel (host shutdown, request
            // abort) keeps the historical Failed marking.
            var wasOperatorCancel = runCancellationToken.IsCancellationRequested;
            _logger.LogInformation(
                "Plan {PlanId} cancelled ({Origin})", planId, wasOperatorCancel ? "operator" : "caller");
            try { await Task.WhenAll(runningTasks); } catch (OperationCanceledException) { }
            MarkRemainingAs(
                stepStates,
                wasOperatorCancel ? StepExecutionStatus.Cancelled : StepExecutionStatus.Failed,
                "Execution cancelled");
        }

        sw.Stop();
        var summary = BuildSummary(planId, stepStates, sw.Elapsed);

        if (summary.FinalStatus == StepExecutionStatus.Failed)
        {
            await NotifyPlanFailureAsync(planId, summary, ct);
            PlanExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "failed"));
        }
        else if (summary.StepStates.All(s => s.Status is StepExecutionStatus.Completed or StepExecutionStatus.Skipped))
        {
            await _notifier.NotifyPlanCompletedAsync(planId, sw.Elapsed, ct);
            PlanExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "completed"));
        }

        return Result<PlanExecutionSummary>.Success(summary);
    }

    /// <summary>
    /// Emits the plan-level failure notification. Reports the first failed step's error when one
    /// exists; otherwise reports an incomplete plan — the scheduler exited with steps that never
    /// reached a terminal state — so the outcome is never silently dropped.
    /// </summary>
    private async Task NotifyPlanFailureAsync(PlanId planId, PlanExecutionSummary summary, CancellationToken ct)
    {
        var failedStep = summary.StepStates.FirstOrDefault(s => s.Status == StepExecutionStatus.Failed);
        if (failedStep is not null)
        {
            await _notifier.NotifyPlanFailedAsync(planId, failedStep.StepId, failedStep.ErrorMessage ?? "Unknown error", ct);
            return;
        }

        var unexecuted = summary.StepStates
            .Where(s => s.Status is StepExecutionStatus.Pending or StepExecutionStatus.Ready or StepExecutionStatus.Running)
            .ToList();
        await _notifier.NotifyPlanFailedAsync(
            planId,
            unexecuted[0].StepId,
            $"Plan did not complete: {unexecuted.Count} step(s) never reached a terminal state",
            ct);
    }
}
