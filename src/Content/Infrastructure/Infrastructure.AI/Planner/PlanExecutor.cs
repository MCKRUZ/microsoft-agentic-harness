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
public sealed class PlanExecutor : IPlanExecutor
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
        }
    }

    public Task<Result> CancelAsync(PlanId planId, CancellationToken ct)
    {
        _logger.LogInformation("Plan {PlanId} cancellation requested", planId);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> RetryStepAsync(PlanId planId, PlanStepId stepId, CancellationToken ct)
    {
        _logger.LogInformation("Retry requested for step {StepId} in plan {PlanId}", stepId, planId);
        return Task.FromResult(Result.Success());
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

    private async Task RunSchedulingLoopAsync(PlanExecutionRuntime ctx, HashSet<Task> runningTasks, CancellationToken ct)
    {
        while (!AllStepsTerminal(ctx.StepStates))
        {
            ct.ThrowIfCancellationRequested();

            while (ctx.ReadyQueue.TryDequeue(out var step))
            {
                await ctx.Concurrency.WaitAsync(ct);
                var task = ExecuteStepAsync(step, ctx, ct);
                runningTasks.Add(task);
            }

            if (runningTasks.Count > 0)
            {
                var completed = await Task.WhenAny(runningTasks);
                runningTasks.Remove(completed);
                await completed;
            }
            else if (HasBlockedSteps(ctx.StepStates) && !HasPendingOrReadySteps(ctx.StepStates))
            {
                break;
            }
            else if (!HasPendingOrReadySteps(ctx.StepStates))
            {
                break;
            }
            else
            {
                _logger.LogWarning("Scheduling loop idle with pending steps — breaking to prevent infinite loop");
                break;
            }
        }

        if (runningTasks.Count > 0)
            await Task.WhenAll(runningTasks);
    }

    private async Task ExecuteStepAsync(PlanStep step, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        using var stepActivity = ActivitySource.StartActivity($"plan.step.{step.Type}");
        stepActivity?.SetTag("step.id", step.Id.Value.ToString());
        stepActivity?.SetTag("step.name", step.Name);
        stepActivity?.SetTag("step.type", step.Type.ToString());

        try
        {
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Running, ctx.StepStates, ct);
            await _notifier.NotifyStepStartedAsync(ctx.PlanId, step.Id, step.Name, step.Type, ct);

            var executor = _serviceProvider.GetRequiredKeyedService<IPlanStepExecutor>(step.Type);
            var upstreamOutputs = GetUpstreamOutputs(step.Id, ctx.DependencyMap, ctx.StepOutputs);

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(step.Timeout);

            var stepSw = Stopwatch.StartNew();
            var result = await executor.ExecuteAsync(step, upstreamOutputs, stepCts.Token);
            stepSw.Stop();

            StepDurationHistogram.Record(stepSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type", step.Type.ToString()));

            await HandleStepResultAsync(step, result, ctx, ct);

            StepExecutionsCounter.Add(1,
                new KeyValuePair<string, object?>("type", step.Type.ToString()),
                new KeyValuePair<string, object?>("status", result.Status.ToString()));

            await _notifier.NotifyStepCompletedAsync(ctx.PlanId, step.Id, result.Status, result.Duration, result.Output, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: "Step timeout exceeded");
            await SkipDownstreamSubgraphAsync(step.Id, ctx);
            StepExecutionsCounter.Add(1,
                new KeyValuePair<string, object?>("type", step.Type.ToString()),
                new KeyValuePair<string, object?>("status", "timeout"));
        }
        catch (OperationCanceledException)
        {
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, CancellationToken.None, errorMessage: "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception executing step {StepId} in plan {PlanId}", step.Id, ctx.PlanId);
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: ex.Message);
            await SkipDownstreamSubgraphAsync(step.Id, ctx);
        }
        finally
        {
            ctx.Concurrency.Release();
        }
    }

    private async Task HandleStepResultAsync(PlanStep step, StepExecutionResult result, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        switch (result.Status)
        {
            case StepExecutionStatus.Completed:
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Completed, ctx.StepStates, ct, output: result.Output);
                if (result.Output is not null)
                    ctx.StepOutputs[step.Id] = result.Output;

                if (step.Type == StepType.ConditionalBranch && result.ActiveEdgeTarget.HasValue)
                    await HandleConditionalBranchAsync(step, result.ActiveEdgeTarget.Value, ctx);
                else
                    await EnqueueReadyDownstreamAsync(step.Id, ctx);
                break;

            case StepExecutionStatus.Blocked:
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Blocked, ctx.StepStates, ct);
                break;

            case StepExecutionStatus.Failed:
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: result.ErrorMessage);
                await HandleStepFailureAsync(step, ctx);
                break;
        }
    }

    private async Task HandleConditionalBranchAsync(PlanStep condStep, PlanStepId activeTarget, PlanExecutionRuntime ctx)
    {
        if (!ctx.DependentMap.TryGetValue(condStep.Id, out var downstream))
            return;

        foreach (var (target, edgeType) in downstream)
        {
            if (target == activeTarget)
            {
                if (ctx.StepLookup.TryGetValue(target, out var targetStep) && TryMarkReady(target, ctx.StepStates))
                {
                    await TransitionStepAsync(ctx.PlanId, target, StepExecutionStatus.Ready, ctx.StepStates, CancellationToken.None);
                    ctx.ReadyQueue.Enqueue(targetStep);
                }
            }
            else if (edgeType is EdgeType.ConditionalTrue or EdgeType.ConditionalFalse)
            {
                await SkipDownstreamSubgraphAsync(target, ctx, includeRoot: true);
            }
        }
    }

    private async Task HandleStepFailureAsync(PlanStep step, PlanExecutionRuntime ctx)
    {
        switch (step.RetryPolicy.OnExhausted)
        {
            case ErrorRecovery.FailStep:
            case ErrorRecovery.Escalate:
                await SkipDownstreamSubgraphAsync(step.Id, ctx);
                break;

            case ErrorRecovery.SkipStep:
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Skipped, ctx.StepStates, CancellationToken.None);
                await EnqueueReadyDownstreamAsync(step.Id, ctx);
                break;

            case ErrorRecovery.FailPlan:
                MarkRemainingAsFailed(ctx.StepStates, "Plan failed due to step failure with FailPlan recovery");
                break;
        }
    }

    private async Task SkipDownstreamSubgraphAsync(PlanStepId fromStepId, PlanExecutionRuntime ctx, bool includeRoot = false)
    {
        var visited = new HashSet<PlanStepId>();
        var queue = new Queue<PlanStepId>();

        if (includeRoot)
        {
            queue.Enqueue(fromStepId);
        }
        else if (ctx.DependentMap.TryGetValue(fromStepId, out var directDownstream))
        {
            foreach (var (target, _) in directDownstream)
                queue.Enqueue(target);
        }

        while (queue.Count > 0)
        {
            var stepId = queue.Dequeue();
            if (!visited.Add(stepId)) continue;

            var currentState = ctx.StepStates.GetValueOrDefault(stepId);
            if (currentState is null || currentState.Status is StepExecutionStatus.Completed or StepExecutionStatus.Failed or StepExecutionStatus.Skipped)
                continue;

            await TransitionStepAsync(ctx.PlanId, stepId, StepExecutionStatus.Skipped, ctx.StepStates, CancellationToken.None);

            if (ctx.DependentMap.TryGetValue(stepId, out var downstream))
            {
                foreach (var (target, _) in downstream)
                    queue.Enqueue(target);
            }
        }
    }

    private async Task EnqueueReadyDownstreamAsync(PlanStepId completedStepId, PlanExecutionRuntime ctx)
    {
        if (!ctx.DependentMap.TryGetValue(completedStepId, out var downstream))
            return;

        foreach (var (target, edgeType) in downstream)
        {
            if (edgeType is EdgeType.ConditionalTrue or EdgeType.ConditionalFalse)
                continue;

            if (!ctx.StepLookup.TryGetValue(target, out var targetStep))
                continue;

            if (!IsStepReady(target, ctx.StepStates, ctx.DependencyMap))
                continue;

            if (TryMarkReady(target, ctx.StepStates))
            {
                await TransitionStepAsync(ctx.PlanId, target, StepExecutionStatus.Ready, ctx.StepStates, CancellationToken.None);
                ctx.ReadyQueue.Enqueue(targetStep);
            }
        }
    }

    private static bool TryMarkReady(PlanStepId stepId, ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
    {
        while (true)
        {
            var current = stepStates.GetValueOrDefault(stepId);
            if (current is null || current.Status != StepExecutionStatus.Pending)
                return false;

            var newState = current with { Status = StepExecutionStatus.Ready };
            if (stepStates.TryUpdate(stepId, newState, current))
                return true;
        }
    }

    private static IReadOnlyDictionary<PlanStepId, string> GetUpstreamOutputs(
        PlanStepId stepId,
        Dictionary<PlanStepId, HashSet<PlanStepId>> dependencyMap,
        ConcurrentDictionary<PlanStepId, string> stepOutputs)
    {
        var outputs = new Dictionary<PlanStepId, string>();
        if (!dependencyMap.TryGetValue(stepId, out var dependencies))
            return outputs;

        foreach (var depId in dependencies)
        {
            if (stepOutputs.TryGetValue(depId, out var output))
                outputs[depId] = output;
        }
        return outputs;
    }

    private async Task TransitionStepAsync(
        PlanId planId,
        PlanStepId stepId,
        StepExecutionStatus newStatus,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        CancellationToken ct,
        string? output = null,
        string? errorMessage = null)
    {
        var previous = stepStates.GetValueOrDefault(stepId);
        var previousStatus = previous?.Status ?? StepExecutionStatus.Pending;

        var newState = new StepExecutionState
        {
            StepId = stepId,
            Status = newStatus,
            AttemptCount = (previous?.AttemptCount ?? 0) + (newStatus == StepExecutionStatus.Running ? 1 : 0),
            StartedAt = newStatus == StepExecutionStatus.Running ? DateTimeOffset.UtcNow : previous?.StartedAt,
            CompletedAt = newStatus is StepExecutionStatus.Completed or StepExecutionStatus.Failed or StepExecutionStatus.Skipped
                ? DateTimeOffset.UtcNow : null,
            Output = output ?? previous?.Output,
            ErrorMessage = errorMessage ?? previous?.ErrorMessage
        };

        stepStates[stepId] = newState;
        await _stateStore.UpdateStepStateAsync(newState, ct);
        await _notifier.NotifyStateUpdateAsync(planId, stepId, previousStatus, newStatus, ct);
    }

    private static void InitializeStepStates(
        PlanGraph plan,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        IReadOnlyDictionary<PlanStepId, StepExecutionState>? existingStates)
    {
        foreach (var step in plan.Steps)
        {
            if (existingStates is not null && existingStates.TryGetValue(step.Id, out var existing))
            {
                var state = existing.Status == StepExecutionStatus.Running
                    ? existing with { Status = StepExecutionStatus.Pending }
                    : existing;
                stepStates[step.Id] = state;
            }
            else
            {
                stepStates[step.Id] = new StepExecutionState
                {
                    StepId = step.Id,
                    Status = StepExecutionStatus.Pending
                };
            }
        }
    }

    private async Task EnqueueInitialReadyStepsAsync(
        PlanGraph plan,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        Dictionary<PlanStepId, HashSet<PlanStepId>> dependencyMap,
        ConcurrentQueue<PlanStep> readyQueue,
        PlanId planId,
        CancellationToken ct)
    {
        foreach (var step in plan.Steps)
        {
            var state = stepStates[step.Id];
            if (state.Status != StepExecutionStatus.Pending)
                continue;

            if (IsStepReady(step.Id, stepStates, dependencyMap) && TryMarkReady(step.Id, stepStates))
            {
                await TransitionStepAsync(planId, step.Id, StepExecutionStatus.Ready, stepStates, ct);
                readyQueue.Enqueue(step);
            }
        }
    }

    private static bool IsStepReady(
        PlanStepId stepId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        Dictionary<PlanStepId, HashSet<PlanStepId>> dependencyMap)
    {
        if (!dependencyMap.TryGetValue(stepId, out var dependencies) || dependencies.Count == 0)
            return true;

        return dependencies.All(depId =>
        {
            var depState = stepStates.GetValueOrDefault(depId);
            return depState?.Status is StepExecutionStatus.Completed or StepExecutionStatus.Skipped;
        });
    }

    private static (Dictionary<PlanStepId, HashSet<PlanStepId>> DependencyMap,
        Dictionary<PlanStepId, List<(PlanStepId Target, EdgeType Type)>> DependentMap) BuildGraphMaps(PlanGraph plan)
    {
        var dependencyMap = new Dictionary<PlanStepId, HashSet<PlanStepId>>();
        var dependentMap = new Dictionary<PlanStepId, List<(PlanStepId, EdgeType)>>();

        foreach (var step in plan.Steps)
        {
            dependencyMap[step.Id] = [];
            dependentMap[step.Id] = [];
        }

        foreach (var edge in plan.Edges)
        {
            dependencyMap[edge.To].Add(edge.From);
            dependentMap[edge.From].Add((edge.To, edge.Type));
        }

        return (dependencyMap, dependentMap);
    }

    private static bool AllStepsTerminal(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.All(s => s.Status is StepExecutionStatus.Completed
            or StepExecutionStatus.Failed
            or StepExecutionStatus.Skipped
            or StepExecutionStatus.Blocked);

    private static bool HasBlockedSteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status == StepExecutionStatus.Blocked);

    private static bool HasPendingOrReadySteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status is StepExecutionStatus.Pending or StepExecutionStatus.Ready);

    private static void MarkRemainingAsFailed(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates, string reason)
    {
        foreach (var (stepId, state) in stepStates)
        {
            if (state.Status is StepExecutionStatus.Pending or StepExecutionStatus.Ready or StepExecutionStatus.Running)
            {
                stepStates[stepId] = state with
                {
                    Status = StepExecutionStatus.Failed,
                    ErrorMessage = reason,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private static PlanExecutionSummary BuildSummary(
        PlanId planId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        TimeSpan totalDuration)
    {
        var states = stepStates.Values.ToList();
        var hasFailures = states.Any(s => s.Status == StepExecutionStatus.Failed);
        var hasBlocked = states.Any(s => s.Status == StepExecutionStatus.Blocked);

        var finalStatus = hasFailures
            ? StepExecutionStatus.Failed
            : hasBlocked
                ? StepExecutionStatus.Blocked
                : StepExecutionStatus.Completed;

        return new PlanExecutionSummary
        {
            PlanId = planId,
            FinalStatus = finalStatus,
            TotalDuration = totalDuration,
            StepStates = states,
            CompletedStepCount = states.Count(s => s.Status == StepExecutionStatus.Completed),
            FailedStepCount = states.Count(s => s.Status == StepExecutionStatus.Failed),
            SkippedStepCount = states.Count(s => s.Status == StepExecutionStatus.Skipped)
        };
    }

    private sealed record PlanExecutionRuntime(
        PlanId PlanId,
        ConcurrentDictionary<PlanStepId, StepExecutionState> StepStates,
        ConcurrentDictionary<PlanStepId, string> StepOutputs,
        Dictionary<PlanStepId, HashSet<PlanStepId>> DependencyMap,
        Dictionary<PlanStepId, List<(PlanStepId Target, EdgeType Type)>> DependentMap,
        Dictionary<PlanStepId, PlanStep> StepLookup,
        ConcurrentQueue<PlanStep> ReadyQueue,
        SemaphoreSlim Concurrency);
}
