using System.Collections.Concurrent;
using System.Diagnostics;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// DAG scheduling logic: graph construction, ready-queue management, step execution dispatch,
/// concurrency control, and scheduling loop.
/// </summary>
public sealed partial class PlanExecutor
{
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
                // Persist the executor's output on the Blocked transition. For a human gate this
                // output carries the escalation identifier, which the resume path
                // (ReconcileBlockedStepsAsync) reads back to correlate the parked step to its
                // escalation. Dropping it here would strand the step permanently in Blocked.
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Blocked, ctx.StepStates, ct, output: result.Output);
                break;

            case StepExecutionStatus.Failed:
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: result.ErrorMessage);
                await HandleStepFailureAsync(step, result.ErrorMessage, ctx, ct);
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

    private static bool AllStepsTerminal(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.All(s => s.Status is StepExecutionStatus.Completed
            or StepExecutionStatus.Failed
            or StepExecutionStatus.Skipped
            or StepExecutionStatus.Blocked
            or StepExecutionStatus.Cancelled);

    private static bool HasBlockedSteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status == StepExecutionStatus.Blocked);

    private static bool HasPendingOrReadySteps(ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates)
        => stepStates.Values.Any(s => s.Status is StepExecutionStatus.Pending or StepExecutionStatus.Ready);
}
