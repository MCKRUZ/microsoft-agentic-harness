using System.Diagnostics;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Domain.Common.Helpers;
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
    // A misconfigured/empty roster is a standing condition, not a per-call event. Warning once
    // keeps the signal without spamming a line on every blocked delegation for the life of the
    // process. Static because escalation config can change between calls but the operator only
    // needs to be told once per process lifetime; Interlocked so concurrent delegations can't
    // both observe unwarned and both log. Mirrors EscalationToolApprovalRouter's
    // s_blankApproversWarned pattern.
    private static int s_delegationApproversWarned;

    // Same rationale as s_delegationApproversWarned, for the tier-2 ("escalated") roster.
    private static int s_delegationEscalatedApproversWarned;

    private async Task<DelegationResult?> HandleAutonomyEscalationAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        int currentDelegationDepth,
        IReadOnlyList<string>? toolOverrides,
        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
        CancellationToken ct)
    {
        // An escalation with nobody on the roster can never be approved — EscalationRequestInvariants
        // rejects it outright, and today that rejection was silently swallowed by the fail-closed
        // catch below, so delegation escalation could never succeed (#393). Refuse immediately with
        // an actionable log instead of raising a request that is guaranteed to be denied.
        var roster = escalationConfig.DelegationApprovers;
        if (roster.Count == 0)
        {
            if (Interlocked.Exchange(ref s_delegationApproversWarned, 1) == 0)
                _logger.LogWarning(
                    "AppConfig:AI:Governance:Escalation:DelegationApprovers is empty — a delegation " +
                    "blocked by autonomy tier can never be escalated for approval and is denied " +
                    "(fail-closed). Configure at least one approver to enable delegation escalation.");

            return null;
        }

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
            // Parsed by member NAME only. A bare Enum.TryParse accepts any integer string, including
            // one outside the defined range, and the strategy is later resolved from keyed DI by its
            // enum value — an undefined value has no registered service and throws at resolution.
            // Same defect, same config keys, third site (#296).
            ApprovalStrategy = EnumNameHelper.TryParseName<ApprovalStrategyType>(
                escalationConfig.DefaultApprovalStrategy, out var strategy)
                ? strategy : ApprovalStrategyType.AnyOf,
            Approvers = roster,
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = EnumNameHelper.TryParseName<EscalationTimeoutAction>(
                escalationConfig.DefaultTimeoutAction, out var timeoutAction)
                ? timeoutAction : EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = DateTimeOffset.UtcNow,
            // #394: if this escalation times out under Escalate/DenyAndEscalate, resolve it as a
            // real tier hand-off instead of a bare denial — recording the tier THIS delegation
            // actually needed (minimumTier), not a fixed constant. HandleEscalatedTierAsync below
            // is the caller-owned downstream process that raises the tier-2 escalation; approving
            // it does not grant minimumTier itself, it only unblocks the retry the same way an
            // ordinary tier-1 approval does (see HandleEscalatedTierAsync's remarks).
            EscalationTierTarget = minimumTier
        };

        if (_escalationService is not { } escalation)
            return null;

        _logger.LogInformation(
            "Autonomy tier violation — escalating delegation for {TaskDescription} (minimumTier: {MinimumTier})",
            taskDescription, minimumTier);

        try
        {
            var outcome = await escalation.RequestEscalationAsync(escalationRequest, ct);

            if (outcome.ResolutionType == EscalationResolutionType.Escalated)
                return await HandleEscalatedTierAsync(
                    outcome, taskDescription, requiredCapabilities, currentDelegationDepth,
                    toolOverrides, escalationConfig, escalation, ct);

            if (!outcome.IsApproved)
            {
                _logger.LogWarning("Escalation {EscalationId} denied for delegation: {TaskDescription}",
                    outcome.EscalationId, taskDescription);
                return null;
            }

            _logger.LogInformation("Escalation {EscalationId} approved — retrying delegation with Restricted tier",
                outcome.EscalationId);

            // +1: an approved-escalation retry is another attempt at the same delegation, not a
            // fresh call — without advancing depth here, an approver who keeps approving (or a
            // misconfigured selector that never finds a match) could retry indefinitely with
            // MaxDelegationDepth never tripping.
            return await DelegateAsync(
                taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
                currentDelegationDepth + 1, toolOverrides, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Escalation service failed for delegation {TaskDescription} — denying (fail-closed)",
                taskDescription);
            return null;
        }
    }

    /// <summary>
    /// The downstream process #394's <c>EscalationResolutionType.Escalated</c> resolution
    /// documents: raises a second, tier-2 escalation to the escalated-approvers roster when the
    /// first tier went unanswered, and only on ITS approval retries the delegation. The retry
    /// drops the autonomy-tier floor to <see cref="AutonomyLevel.Restricted"/> — the same relief
    /// an ordinary tier-1 approval grants (see <see cref="CapabilityMatchStrategy"/>'s
    /// <c>MinimumAutonomyLevel</c> floor-exclusion filter: a human approval means "let this
    /// proceed with whatever agent is available," not "grant a specific higher tier" — there is
    /// no domain concept of granting a tier here, only of relaxing the automated pre-filter). An
    /// empty escalated roster denies (fail-closed) — the same posture as an empty tier-1 roster in
    /// <see cref="HandleAutonomyEscalationAsync"/>.
    /// </summary>
    private async Task<DelegationResult?> HandleEscalatedTierAsync(
        EscalationOutcome tier1Outcome,
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        int currentDelegationDepth,
        IReadOnlyList<string>? toolOverrides,
        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
        IEscalationService escalation,
        CancellationToken ct)
    {
        var escalatedRoster = escalationConfig.DelegationEscalatedApprovers;
        if (escalatedRoster.Count == 0)
        {
            if (Interlocked.Exchange(ref s_delegationEscalatedApproversWarned, 1) == 0)
                _logger.LogWarning(
                    "Escalation {EscalationId} was escalated to tier {Tier} but " +
                    "AppConfig:AI:Governance:Escalation:DelegationEscalatedApprovers is empty — " +
                    "denying (fail-closed). Configure at least one escalated approver to enable it.",
                    tier1Outcome.EscalationId, tier1Outcome.EscalatedToTier);

            return null;
        }

        var tier2Request = new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = SupervisorId,
            ToolName = $"delegate:{string.Join(",", requiredCapabilities)}",
            Arguments = new Dictionary<string, string> { ["taskDescription"] = taskDescription },
            Description =
                $"Escalated (autonomy tier {tier1Outcome.EscalatedToTier} unmet, first tier " +
                $"unanswered): {taskDescription}",
            RiskLevel = RiskLevel.High,
            Priority = EscalationPriority.Critical,
            // Same config-driven parsing as the tier-1 request above — a configured approval
            // strategy or timeout action should apply uniformly to both tiers, not silently weaken
            // at the more sensitive, higher-priority tier. TimeoutAction is deliberately never
            // itself escalatable here: EscalationTierTarget is left unset on this request, so even
            // a configured Escalate/DenyAndEscalate action resolves as an ordinary timeout denial,
            // never a third escalation tier.
            ApprovalStrategy = EnumNameHelper.TryParseName<ApprovalStrategyType>(
                escalationConfig.DefaultApprovalStrategy, out var tier2Strategy)
                ? tier2Strategy : ApprovalStrategyType.AnyOf,
            Approvers = escalatedRoster,
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = EnumNameHelper.TryParseName<EscalationTimeoutAction>(
                escalationConfig.DefaultTimeoutAction, out var tier2TimeoutAction)
                ? tier2TimeoutAction : EscalationTimeoutAction.Deny,
            RequestedAt = DateTimeOffset.UtcNow,
            PredecessorEscalationId = tier1Outcome.EscalationId
        };

        _logger.LogInformation(
            "Escalation {EscalationId} escalated to tier {Tier} — requesting approval from " +
            "{Count} escalated approver(s)",
            tier1Outcome.EscalationId, tier1Outcome.EscalatedToTier, escalatedRoster.Count);

        var tier2Outcome = await escalation.RequestEscalationAsync(tier2Request, ct);

        if (!tier2Outcome.IsApproved)
        {
            _logger.LogWarning(
                "Escalated-tier escalation {EscalationId} denied for delegation: {TaskDescription}",
                tier2Outcome.EscalationId, taskDescription);
            return null;
        }

        _logger.LogInformation(
            "Escalated-tier escalation {EscalationId} approved — retrying delegation with Restricted tier",
            tier2Outcome.EscalationId);

        // Restricted, not tier1Outcome.EscalatedToTier — see this method's remarks. +1 for the
        // same unbounded-retry reason as the ordinary-approval retry in HandleAutonomyEscalationAsync.
        return await DelegateAsync(
            taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
            currentDelegationDepth + 1, toolOverrides, ct);
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
