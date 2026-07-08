using System.Collections.Concurrent;
using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Failure handling and flow control: step failure recovery (including escalation),
/// downstream subgraph skipping, blocked-step reconciliation, state transitions, and step
/// initialization.
/// </summary>
public sealed partial class PlanExecutor
{
    /// <summary>
    /// JSON property under which a blocked step's escalation identifier is stored in its output.
    /// Must match the shape written by <c>HumanGateStepExecutor</c> (<c>{ "escalationId": "&lt;guid&gt;" }</c>)
    /// and by the escalate failure-recovery branch below.
    /// </summary>
    private const string EscalationIdProperty = "escalationId";
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
                // Persist the escalation id so the resume path can resolve this block. Without it the
                // escalated step would be a permanent dead end exactly like an unpersisted human gate.
                await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Blocked, ctx.StepStates, ct,
                    output: SerializeEscalationRef(escalationId));

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

    /// <summary>
    /// Re-evaluates every step currently parked in <see cref="StepExecutionStatus.Blocked"/> against
    /// the resolution of the escalation that blocked it. This is the bridge that lets a human gate (or
    /// an escalate-on-failure step) leave <c>Blocked</c> once a decision arrives, and it runs on each
    /// re-entry into execution (resume): the plan blocks, the caller re-invokes
    /// <c>ExecuteAsync</c> after the escalation resolves, and this reconciliation acts on the verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two distinct producers park a step in <c>Blocked</c>, and an <b>approval</b> means different
    /// things for each — so they are handled differently:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Human gate</b> (<see cref="StepType.HumanGate"/>) — a pure approval
    ///   checkpoint with no work of its own. Approval <b>completes</b> it and releases its
    ///   downstream. Its stored output holds only the escalation reference, which is never propagated
    ///   as step output.</description></item>
    ///   <item><description><b>Escalate-on-failure</b> block (a step whose real execution failed after
    ///   exhausting retries and whose <see cref="ErrorRecovery.Escalate"/> policy then escalated) —
    ///   approval means "let the real work run again", so the step is reset to <c>Pending</c> and
    ///   re-executed by the scheduler; its downstream then receives the genuine output. A repeat
    ///   failure re-escalates per the step's retry policy. It is <b>never</b> marked Completed without
    ///   re-running, and its escalation-reference output is never fed downstream.</description></item>
    /// </list>
    /// <para>Rejection is uniform: the step is failed and its downstream skipped terminally (a human
    /// "no" is not re-run through the retry policy — that could re-escalate the rejected request). An
    /// unresolved escalation (null outcome) leaves the step <c>Blocked</c>; a blocked step without a
    /// recoverable escalation id is left untouched.</para>
    /// <para>This covers resume-time resolution; unblocking mid-execution the instant an approval
    /// lands is a tracked follow-up — the scheduler currently drains when only blocked steps
    /// remain.</para>
    /// </remarks>
    private async Task ReconcileBlockedStepsAsync(PlanGraph plan, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        foreach (var step in plan.Steps)
        {
            var state = ctx.StepStates.GetValueOrDefault(step.Id);
            if (state is null || state.Status != StepExecutionStatus.Blocked)
                continue;

            var escalationId = TryReadEscalationId(state.Output);
            if (escalationId is null)
                continue;

            var outcome = await _escalationService.GetOutcomeAsync(escalationId.Value, ct);
            if (outcome is null)
                continue;

            if (outcome.IsApproved)
                await ReleaseApprovedBlockAsync(step, escalationId.Value, ctx, ct);
            else
                await FailRejectedBlockAsync(step, escalationId.Value, outcome, ctx, ct);
        }
    }

    /// <summary>
    /// Releases a blocked step whose escalation was approved. A human gate is completed and its
    /// downstream released; an escalate-on-failure block is reset to <c>Pending</c> so its real work
    /// re-runs. In neither case is the escalation-reference output propagated as the step's output —
    /// a gate produces no data, and a re-run produces its own genuine output.
    /// </summary>
    private async Task ReleaseApprovedBlockAsync(PlanStep step, Guid escalationId, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        if (step.Type == StepType.HumanGate)
        {
            _logger.LogInformation(
                "Human gate {StepId} in plan {PlanId} released: escalation {EscalationId} approved",
                step.Id, ctx.PlanId, escalationId);

            // Complete the gate and clear the escalation-reference output so it is never mistaken for
            // real data. Downstream is released via EnqueueReadyDownstreamAsync, not via StepOutputs.
            await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Completed, ctx.StepStates, ct,
                output: null, clearPriorOutputs: true);
            await EnqueueReadyDownstreamAsync(step.Id, ctx);
            return;
        }

        _logger.LogInformation(
            "Escalated failure of step {StepId} in plan {PlanId} approved (escalation {EscalationId}); re-running the step",
            step.Id, ctx.PlanId, escalationId);

        // The step's real work failed; approval authorizes another attempt. Reset to Pending (clearing
        // the stale error and escalation-reference output) so the scheduler re-runs it — the canonical
        // Pending -> Ready promotion picks it up in EnqueueInitialReadyStepsAsync. Downstream then
        // receives the genuine re-run output; a repeat failure re-escalates per the retry policy.
        await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Pending, ctx.StepStates, ct,
            output: null, clearPriorOutputs: true);
    }

    /// <summary>
    /// Fails a blocked step whose escalation was rejected (denied or timed out) and skips its
    /// downstream subgraph. A rejection is a terminal human decision, so it deliberately does NOT
    /// route back through <see cref="HandleStepFailureAsync"/>: that would re-run the step's retry
    /// policy, and an <see cref="ErrorRecovery.Escalate"/> policy would re-escalate the very request
    /// the human just rejected — an unbounded reject → re-escalate loop across resumes.
    /// </summary>
    private async Task FailRejectedBlockAsync(
        PlanStep step, Guid escalationId, EscalationOutcome outcome, PlanExecutionRuntime ctx, CancellationToken ct)
    {
        var reason = $"Escalation {escalationId} was not approved (resolution: {outcome.ResolutionType}).";
        _logger.LogWarning(
            "Blocked step {StepId} in plan {PlanId} failed: {Reason}", step.Id, ctx.PlanId, reason);

        await TransitionStepAsync(ctx.PlanId, step.Id, StepExecutionStatus.Failed, ctx.StepStates, ct, errorMessage: reason);
        await SkipDownstreamSubgraphAsync(step.Id, ctx);
    }

    /// <summary>
    /// Serializes an escalation identifier into the JSON output shape stored on a blocked step,
    /// matching the format read by <see cref="TryReadEscalationId"/>.
    /// </summary>
    private static string SerializeEscalationRef(Guid escalationId)
        => JsonSerializer.Serialize(new Dictionary<string, string> { [EscalationIdProperty] = escalationId.ToString() });

    /// <summary>
    /// Extracts the escalation identifier previously persisted in a blocked step's output, or null
    /// when the output is absent, malformed, or carries no escalation reference.
    /// </summary>
    private static Guid? TryReadEscalationId(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(EscalationIdProperty, out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(idElement.GetString(), out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Non-JSON or corrupt output — no escalation reference to recover.
        }

        return null;
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

    /// <summary>
    /// Persists a step-state transition and notifies observers. By default <paramref name="output"/>
    /// and <paramref name="errorMessage"/> fall back to the prior state when null (a status change
    /// preserves existing data). Set <paramref name="clearPriorOutputs"/> to force the new values —
    /// including null — so a step can be genuinely cleared of stale output/error (used when a blocked
    /// step is completed or reset for re-run, to stop an escalation reference lingering as data).
    /// </summary>
    private async Task TransitionStepAsync(
        PlanId planId,
        PlanStepId stepId,
        StepExecutionStatus newStatus,
        ConcurrentDictionary<PlanStepId, StepExecutionState> stepStates,
        CancellationToken ct,
        string? output = null,
        string? errorMessage = null,
        bool clearPriorOutputs = false)
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
            Output = clearPriorOutputs ? output : (output ?? previous?.Output),
            ErrorMessage = clearPriorOutputs ? errorMessage : (errorMessage ?? previous?.ErrorMessage)
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
                // A resumed step that was in-flight (Running) or merely queued (Ready) when the host
                // crashed must be renormalised to Pending so the scheduler re-evaluates its
                // dependencies and re-enqueues it through the canonical Pending -> Ready promotion
                // path. Leaving it as Ready would strand it: EnqueueInitialReadyStepsAsync only picks
                // up Pending steps, so the step would never run yet the plan would report Completed.
                var state = existing.Status is StepExecutionStatus.Running or StepExecutionStatus.Ready
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
