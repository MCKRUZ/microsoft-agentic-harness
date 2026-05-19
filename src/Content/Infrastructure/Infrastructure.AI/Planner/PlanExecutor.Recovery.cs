using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Failure handling and flow control: step failure recovery (including escalation),
/// downstream subgraph skipping, state transitions, and step initialization.
/// </summary>
public sealed partial class PlanExecutor
{
    private async Task HandleStepFailureAsync(PlanStep step, string? errorMessage, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        switch (step.RetryPolicy.OnExhausted)
        {
            case ErrorRecovery.FailStep:
                await SkipDownstreamSubgraphAsync(step.Id, ctx);
                break;

            case ErrorRecovery.Escalate:
                var request = new EscalationRequest
                {
                    EscalationId = Guid.NewGuid(),
                    AgentId = "plan-executor",
                    ToolName = step.Name,
                    Arguments = new Dictionary<string, string>
                    {
                        ["stepId"] = step.Id.Value.ToString(),
                        ["stepType"] = step.Type.ToString()
                    },
                    Description = errorMessage ?? $"Step '{step.Name}' failed after exhausting retries",
                    RiskLevel = RiskLevel.High,
                    Priority = EscalationPriority.Blocking,
                    ApprovalStrategy = ApprovalStrategyType.AnyOf,
                    Approvers = ["supervisor"],
                    RequestedAt = DateTimeOffset.UtcNow
                };

                var escalationId = await _escalationService.QueueEscalationAsync(request, ct);
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Blocked, ctx.StepStates, ct);

                _logger.LogWarning(
                    "Step {StepId} in plan {PlanId} escalated as {EscalationId} — step blocked pending human approval",
                    step.Id, ctx.PlanId, escalationId);
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
            CompletedAt = newStatus is StepExecutionStatus.Completed or StepExecutionStatus.Failed or StepExecutionStatus.Skipped or StepExecutionStatus.Cancelled
                ? DateTimeOffset.UtcNow : null,
            Output = output ?? previous?.Output,
            ErrorMessage = errorMessage ?? previous?.ErrorMessage
        };

        stepStates[stepId] = newState;
        var updateResult = await _stateStore.UpdateStepStateAsync(newState, ct);
        if (!updateResult.IsSuccess)
            _logger.LogError("Failed to persist state for step {StepId}: {Errors}", stepId, string.Join(", ", updateResult.Errors));
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
}
