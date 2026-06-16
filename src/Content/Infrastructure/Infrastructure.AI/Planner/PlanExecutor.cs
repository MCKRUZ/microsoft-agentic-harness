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
/// scheduling with bounded concurrency, checkpoint/resume, blocked step polling,
/// conditional branching, and per-plan serialization.
/// </summary>
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
    private readonly ILogger<PlanExecutor> _logger;

    public PlanExecutor(
        IPlanValidator validator,
        IPlanStateStore stateStore,
        IPlanProgressNotifier notifier,
        IEscalationService escalationService,
        IServiceProvider serviceProvider,
        ILogger<PlanExecutor> logger)
    {
        _validator = validator;
        _stateStore = stateStore;
        _notifier = notifier;
        _escalationService = escalationService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct)
        => ExecuteAsync(planId, new PlanExecutionContext(), ct);

    public async Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, PlanExecutionContext context, CancellationToken ct)
    {
        if (context.Depth >= context.MaxDepth)
            return Result<PlanExecutionSummary>.Fail($"Maximum sub-plan depth {context.MaxDepth} exceeded at depth {context.Depth}.");

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
            return await ExecuteCoreAsync(planId, context, ct);
        }
        finally
        {
            ReleasePlanLock(planId, planLock);
        }
    }

    public async Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
    {
        _logger.LogInformation("Plan {PlanId} cancellation requested", planId);

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

            var resetState = new StepExecutionState
            {
                StepId = stepId,
                Status = StepExecutionStatus.Ready,
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
                "Step {StepId} in plan {PlanId} reset to Ready for retry (attempt {AttemptCount} total)",
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

    private async Task<Result<PlanExecutionSummary>> ExecuteCoreAsync(PlanId planId, PlanExecutionContext context, CancellationToken ct)
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
        await EnqueueInitialReadyStepsAsync(plan, stepStates, dependencyMap, readyQueue, planId, planCts.Token);

        using var concurrency = new SemaphoreSlim(plan.Configuration.MaxParallelSteps, plan.Configuration.MaxParallelSteps);
        var runningTasks = new HashSet<Task>();

        var execCtx = new PlanExecutionRuntime(
            planId, stepStates, stepOutputs, dependencyMap, dependentMap, stepLookup, readyQueue, concurrency);

        try
        {
            await RunSchedulingLoopAsync(execCtx, runningTasks, planCts.Token);
        }
        catch (OperationCanceledException) when (planCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Plan {PlanId} timed out after {Timeout}", planId, plan.Configuration.PlanTimeout);
            try { await Task.WhenAll(runningTasks); } catch (OperationCanceledException) { }
            MarkRemainingAsFailed(stepStates, "Plan timeout exceeded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Plan {PlanId} cancelled by caller", planId);
            try { await Task.WhenAll(runningTasks); } catch (OperationCanceledException) { }
            MarkRemainingAsFailed(stepStates, "Execution cancelled");
        }

        sw.Stop();
        var summary = BuildSummary(planId, stepStates, sw.Elapsed);

        if (summary.FailedStepCount > 0)
        {
            var failedStep = summary.StepStates.First(s => s.Status == StepExecutionStatus.Failed);
            await _notifier.NotifyPlanFailedAsync(planId, failedStep.StepId, failedStep.ErrorMessage ?? "Unknown error", ct);
            PlanExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "failed"));
        }
        else if (summary.StepStates.All(s => s.Status is StepExecutionStatus.Completed or StepExecutionStatus.Skipped))
        {
            await _notifier.NotifyPlanCompletedAsync(planId, sw.Elapsed, ct);
            PlanExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "completed"));
        }

        return Result<PlanExecutionSummary>.Success(summary);
    }
}
