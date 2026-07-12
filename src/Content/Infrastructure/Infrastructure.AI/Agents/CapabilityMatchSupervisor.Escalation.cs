using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Domain.AI.Agents;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Agents;

public sealed partial class CapabilityMatchSupervisor
{
    private async Task<DelegationResult?> HandleAutonomyEscalationAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDelegationDepth,
        IReadOnlyList<string>? toolOverrides,
        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
        CancellationToken ct)
    {
        var escalationRequest = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = SupervisorId,
            ToolName = $"delegate:{string.Join(",", requiredCapabilities)}",
            Arguments = new Dictionary<string, string>
            {
                ["taskDescription"] = taskDescription,
                ["minimumTier"] = minimumTier.ToString()
            },
            Description = $"Delegation blocked by autonomy tier ({minimumTier}): {taskDescription}",
            RiskLevel = RiskLevel.Medium,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = Enum.TryParse<ApprovalStrategyType>(
                escalationConfig.DefaultApprovalStrategy, true, out var strategy)
                ? strategy : ApprovalStrategyType.AnyOf,
            Approvers = [],
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = Enum.TryParse<EscalationTimeoutAction>(
                escalationConfig.DefaultTimeoutAction, true, out var timeoutAction)
                ? timeoutAction : EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = DateTimeOffset.UtcNow
        };

        if (_escalationService is not { } escalation)
            return null;

        _logger.LogInformation(
            "Autonomy tier violation — escalating delegation for {TaskDescription} (minimumTier: {MinimumTier})",
            taskDescription, minimumTier);

        try
        {
            var outcome = await escalation.RequestEscalationAsync(escalationRequest, ct);

            if (!outcome.IsApproved)
            {
                _logger.LogWarning("Escalation {EscalationId} denied for delegation: {TaskDescription}",
                    outcome.EscalationId, taskDescription);
                return null;
            }

            _logger.LogInformation("Escalation {EscalationId} approved — retrying delegation with Restricted tier",
                outcome.EscalationId);

            return await DelegateAsync(
                taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
                currentDelegationDepth, toolOverrides, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Escalation service failed for delegation {TaskDescription} — denying (fail-closed)",
                taskDescription);
            return null;
        }
    }

    private async Task<DelegationResult> ExecuteAndTrack(
        Guid delegationId,
        DelegationRecord pendingRecord,
        AgentSelection selection,
        IReadOnlyList<string>? toolOverrides,
        int currentDepth,
        int timeoutSeconds,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var acquired = await _concurrencySemaphore.WaitAsync(
            TimeSpan.FromSeconds(timeoutSeconds), ct);

        if (!acquired)
        {
            await RecordFailure(pendingRecord, "Concurrency semaphore acquisition timed out.", ct);
            return DelegationResult.Fail("Concurrency semaphore acquisition timed out.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        _activeDelegations[delegationId] = cts;

        try
        {
            return await ExecuteAgent(pendingRecord, selection, toolOverrides, currentDepth, stopwatch, cts.Token);
        }
        catch (OperationCanceledException)
        {
            var reason = ct.IsCancellationRequested ? "Delegation cancelled." : "Delegation timed out.";
            await RecordCancellation(pendingRecord, reason, ct);
            return DelegationResult.Fail(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delegation {DelegationId} to {AgentId} failed",
                delegationId, selection.SelectedAgent.AgentId);
            await RecordFailure(pendingRecord, ex.Message, ct);
            return DelegationResult.Fail(ex.Message);
        }
        finally
        {
            _activeDelegations.TryRemove(delegationId, out _);
            _concurrencySemaphore.Release();
        }
    }

    private async Task<DelegationResult> ExecuteAgent(
        DelegationRecord pendingRecord,
        AgentSelection selection,
        IReadOnlyList<string>? toolOverrides,
        int currentDepth,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var definition = _profileRegistry.GetProfile(selection.SelectedAgent.AgentType);
        var agentContext = _contextFactory.CreateFromDelegation(definition, toolOverrides, currentDepth + 1, pendingRecord.DelegationId);
        var agent = await _agentFactory.CreateAgentAsync(agentContext, ct);

        // Run the delegated subagent on the task, isolating its usage accounting from the parent turn.
        // Creating the agent alone does no work — the task description must be sent as the subagent's
        // user message and its response captured, otherwise the delegation returns a placeholder and the
        // orchestrator has nothing to synthesize (the defect behind GitHub #96, Issue 2). The subagent
        // now carries real tools, and the parent orchestrator turn has its own LlmUsageCapture set as the
        // ambient AsyncLocal for the duration of its RunAsync; the subagent runs *inside* that turn (as a
        // tool call), so without swapping the ambient here the subagent's tokens AND tool invocations
        // would fold into the ORCHESTRATOR turn's telemetry — it would report tool calls it never made.
        // A fresh capture scopes the subagent's work to this delegation and yields its real token cost.
        // (Tool-invocation governance/progress ambients are intentionally left as-is; per-subagent
        // governance re-scoping under enforcement is tracked as a follow-up — see the PR description.)
        var delegationUsage = new LlmUsageCapture(_options);
        var previousUsage = LlmUsageCapture.Current;
        LlmUsageCapture.Current = delegationUsage;
        AgentResponse response;
        try
        {
            response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, pendingRecord.TaskDescription)],
                cancellationToken: ct);
        }
        finally
        {
            LlmUsageCapture.Current = previousUsage;
        }

        stopwatch.Stop();

        await RecordCompletion(pendingRecord, ct);

        var durationMs = stopwatch.ElapsedMilliseconds;
        var usage = delegationUsage.TakeSnapshot();

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, selection.SelectedAgent.AgentId),
            new(SupervisorConventions.Outcome, "completed"));

        SupervisorMetrics.DelegationDuration.Record(durationMs,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, selection.SelectedAgent.AgentId));

        _auditService.Log(
            SupervisorId,
            $"completed:{selection.SelectedAgent.AgentId}",
            $"delegation {pendingRecord.DelegationId} completed in {durationMs}ms");

        _logger.LogInformation(
            "Delegation {DelegationId} to {AgentId} completed in {DurationMs}ms",
            pendingRecord.DelegationId, selection.SelectedAgent.AgentId, durationMs);

        var output = response.Text ?? string.Empty;
        return DelegationResult.Success(output, usage.InputTokens + usage.OutputTokens, durationMs);
    }

    private async Task RecordCompletion(DelegationRecord pendingRecord, CancellationToken ct)
    {
        var record = pendingRecord with
        {
            State = DelegationState.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _delegationStore.AppendAsync(record, ct);
    }

    private async Task RecordFailure(DelegationRecord pendingRecord, string reason, CancellationToken ct)
    {
        _auditService.Log(SupervisorId, $"failed:{pendingRecord.DelegateAgentId}", reason);

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, pendingRecord.DelegateAgentId),
            new(SupervisorConventions.Outcome, "failed"));

        var record = pendingRecord with
        {
            State = DelegationState.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };

        await _delegationStore.AppendAsync(record, ct);
    }

    private async Task RecordCancellation(DelegationRecord pendingRecord, string reason, CancellationToken ct)
    {
        _auditService.Log(SupervisorId, $"cancelled:{pendingRecord.DelegateAgentId}", reason);

        SupervisorMetrics.DelegationsTotal.Add(1,
            new(SupervisorConventions.SupervisorId, SupervisorId),
            new(SupervisorConventions.DelegateAgentId, pendingRecord.DelegateAgentId),
            new(SupervisorConventions.Outcome, "cancelled"));

        var record = pendingRecord with
        {
            State = DelegationState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };

        await _delegationStore.AppendAsync(record, ct);
    }
}
