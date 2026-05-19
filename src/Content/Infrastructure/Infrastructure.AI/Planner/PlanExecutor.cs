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

    private static readonly ConcurrentDictionary<PlanId, SemaphoreSlim> PlanLocks = new();

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

        var planLock = PlanLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1));
        await planLock.WaitAsync(ct);
        try
        {
            return await ExecuteCoreAsync(planId, context, ct);
        }
        finally
        {
            planLock.Release();
            if (planLock.CurrentCount == 1 && PlanLocks.TryRemove(planId, out var removed))
                removed.Dispose();
        }
    }

    public async Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
    {
        _logger.LogInformation("Plan {PlanId} cancellation requested", planId);

        var planLock = PlanLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1));
        await planLock.WaitAsync(ct);
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
            planLock.Release();
            if (planLock.CurrentCount == 1 && PlanLocks.TryRemove(planId, out var removed))
                removed.Dispose();
        }
    }

    public async Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
    {
        _logger.LogInformation("Retry requested for step {StepId} in plan {PlanId}", stepId, planId);

        var planLock = PlanLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1));
        await planLock.WaitAsync(ct);
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
            planLock.Release();
            if (planLock.CurrentCount == 1 && PlanLocks.TryRemove(planId, out var removed))
                removed.Dispose();
        }
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
