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
            await RunStepWithRetriesAsync(step, ctx, ct);
        }
        catch (OperationCanceledException)
        {
            // Plan-level cancellation (caller token or plan timeout) — deliberately NOT retryable.
            // Per-attempt timeouts never surface here: ExecuteSingleAttemptAsync converts them into
            // failed attempts, so an OperationCanceledException at this level always means the
            // whole plan is being torn down (including cancellation during a backoff delay).
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, CancellationToken.None, errorMessage: "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Non-attempt infrastructure failure (executor resolution, state persistence,
            // notification). Attempt-level executor exceptions are consumed by the retry loop, so
            // anything reaching here is outside the retry budget: fail the step and skip downstream.
            _logger.LogError(ex, "Unhandled exception executing step {StepId} in plan {PlanId}", step.Id, ctx.PlanId);
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: ex.Message);
            await SkipDownstreamSubgraphAsync(step.Id, ctx);
        }
        finally
        {
            ctx.Concurrency.Release();
        }
    }

    /// <summary>
    /// Drives the attempt/retry loop for a single step, honouring its <see cref="RetryPolicy"/>.
    /// Each failed attempt — a <see cref="StepExecutionStatus.Failed"/> result, a per-attempt
    /// timeout, or an unhandled executor exception — consumes one unit of the
    /// <see cref="RetryPolicy.MaxRetries"/> budget; the next attempt starts after the backoff
    /// delay dictated by <see cref="RetryPolicy.Strategy"/>. Only when the budget is exhausted
    /// does the step transition to Failed and its <see cref="RetryPolicy.OnExhausted"/> recovery
    /// fire. A <see cref="StepExecutionStatus.Blocked"/> result (human gate) is a park, not a
    /// failure, and is never retried.
    /// </summary>
    /// <remarks>
    /// The retry budget is tracked through the persisted <see cref="StepExecutionState.AttemptCount"/>,
    /// which increments on every Running transition — so attempts made before a crash count after
    /// resume, and each re-attempt is checkpointed before it executes (at-least-once semantics per
    /// <see cref="IPlanStepExecutor.ExecuteAsync"/>). Step-started is notified once per scheduling
    /// pass; individual retry attempts surface through the Running-transition state updates.
    /// </remarks>
    private async Task RunStepWithRetriesAsync(PlanStep step, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        var executor = _serviceProvider.GetRequiredKeyedService<IPlanStepExecutor>(step.Type);
        var upstreamOutputs = GetUpstreamOutputs(step.Id, ctx.DependencyMap, ctx.StepOutputs);
        var firstIteration = true;

        while (true)
        {
            // Each attempt transitions to Running, which increments and persists AttemptCount —
            // checkpoint/resume therefore never loses retry progress.
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Running, ctx.StepStates, ct);
            if (firstIteration)
            {
                await _notifier.NotifyStepStartedAsync(ctx.PlanId, step.Id, step.Name, step.Type, ct);
                firstIteration = false;
            }

            var attemptsMade = ctx.StepStates[step.Id].AttemptCount;
            var outcome = await ExecuteSingleAttemptAsync(step, executor, upstreamOutputs, ctx, ct);

            if (outcome.Result is not null && outcome.Result.Status != StepExecutionStatus.Failed)
            {
                await HandleStepResultAsync(step, outcome.Result, ctx, ct);
                await _notifier.NotifyStepCompletedAsync(ctx.PlanId, step.Id, outcome.Result.Status, outcome.Result.Duration, outcome.Result.Output, ct);
                return;
            }

            if (ShouldRetry(step.RetryPolicy, attemptsMade))
            {
                _logger.LogWarning(
                    "Step {StepId} in plan {PlanId} failed attempt {Attempt} of {MaxAttempts} ({Error}); retrying after backoff",
                    step.Id, ctx.PlanId, attemptsMade, step.RetryPolicy.MaxRetries + 1, outcome.ErrorMessage);
                await DelayBeforeRetryAsync(step.RetryPolicy, attemptsMade, ct);
                continue;
            }

            await FailExhaustedStepAsync(step, outcome, ctx, ct);
            return;
        }
    }

    /// <summary>
    /// Executes one attempt of a step under its per-attempt timeout and converts every retryable
    /// failure mode into a <see cref="StepAttemptOutcome"/> instead of an exception. The step
    /// <see cref="PlanStep.Timeout"/> applies PER ATTEMPT — every retry gets the full timeout
    /// budget again, so a step's worst-case wall clock is (MaxRetries + 1) × Timeout plus backoff
    /// delays, all still bounded by the plan-level timeout carried on <paramref name="ct"/>.
    /// Plan-level cancellation is rethrown untouched (never retryable).
    /// </summary>
    private async Task<StepAttemptOutcome> ExecuteSingleAttemptAsync(
        PlanStep step,
        IPlanStepExecutor executor,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        PlanExecutionRuntime ctx,
        CancellationToken ct)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(step.Timeout);

        try
        {
            var stepSw = Stopwatch.StartNew();
            var result = await executor.ExecuteAsync(step, upstreamOutputs, stepCts.Token);
            stepSw.Stop();

            StepDurationHistogram.Record(stepSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type", step.Type.ToString()));
            StepExecutionsCounter.Add(1,
                new KeyValuePair<string, object?>("type", step.Type.ToString()),
                new KeyValuePair<string, object?>("status", result.Status.ToString()));

            return new StepAttemptOutcome(result, SynthesizedError: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-attempt timeout — counts as a failed, retryable attempt.
            StepExecutionsCounter.Add(1,
                new KeyValuePair<string, object?>("type", step.Type.ToString()),
                new KeyValuePair<string, object?>("status", "timeout"));
            return new StepAttemptOutcome(null, "Step timeout exceeded");
        }
        catch (OperationCanceledException)
        {
            // Plan-level cancellation — never retried; handled by ExecuteStepAsync.
            throw;
        }
        catch (Exception ex)
        {
            // Unhandled executor exception — counts as a failed, retryable attempt.
            _logger.LogError(ex, "Unhandled exception executing step {StepId} in plan {PlanId} (attempt will consume retry budget)", step.Id, ctx.PlanId);
            return new StepAttemptOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Outcome of a single execution attempt: either the executor's result, or — when
    /// <see cref="Result"/> is null — a synthesized error for a per-attempt timeout or an
    /// unhandled executor exception. Both null-result shapes and a Failed result are retryable.
    /// </summary>
    /// <summary>
    /// The outcome of one step attempt: the executor's result when it produced one, or a
    /// synthesized error for attempts that never returned (timeout, unhandled exception).
    /// <see cref="ErrorMessage"/> derives from whichever is present, so there is a single
    /// source of truth for the failure text.
    /// </summary>
    private sealed record StepAttemptOutcome(StepExecutionResult? Result, string? SynthesizedError)
    {
        /// <summary>The attempt's failure text: the executor's own error, else the synthesized one.</summary>
        public string? ErrorMessage => Result?.ErrorMessage ?? SynthesizedError;
    }

    /// <summary>
    /// Applies a successful (non-failure) attempt result: Completed steps propagate output and
    /// release downstream, Blocked steps are parked. Failure routing deliberately does NOT pass
    /// through here — it lives in the retry loop (<see cref="RunStepWithRetriesAsync"/>) so that
    /// <see cref="RetryPolicy.OnExhausted"/> fires only after the retry budget is spent.
    /// </summary>
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
