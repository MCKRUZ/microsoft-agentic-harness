using System.Diagnostics;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Tools;
using Domain.Common.Helpers;
using Domain.AI.Agents;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.AI.Skills;
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
    // needs to be told once per process lifetime. See WarnOnceGate for the exchange-and-guard
    // mechanics shared with EscalationToolApprovalRouter's own two warn-once fields.
    private static int s_delegationApproversWarned;

    // Same rationale as s_delegationApproversWarned, for the tier-2 ("escalated") roster.
    private static int s_delegationEscalatedApproversWarned;

    /// <summary>
    /// Builds a delegation-escalation <see cref="EscalationRequest"/>, sharing the config-driven
    /// <see cref="ApprovalStrategyType"/>/<see cref="EscalationTimeoutAction"/> parsing (and its
    /// parse-by-name invariant, #296) and the fields common to both the tier-1 request in
    /// <see cref="HandleAutonomyEscalationAsync"/> and the tier-2 request in
    /// <see cref="HandleEscalatedTierAsync"/>.
    /// </summary>
    private EscalationRequest BuildDelegationEscalationRequest(
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyDictionary<string, string> arguments,
        string description,
        RiskLevel riskLevel,
        EscalationPriority priority,
        IReadOnlyList<string> approvers,
        EscalationTimeoutAction fallbackTimeoutAction,
        Domain.Common.Config.AI.Governance.EscalationConfig escalationConfig,
        AutonomyLevel? escalationTierTarget = null,
        Guid? predecessorEscalationId = null) => new()
        {
            EscalationId = Guid.NewGuid(),
            AgentId = SupervisorId,
            ToolName = $"delegate:{string.Join(",", requiredCapabilities)}",
            Arguments = arguments,
            Description = description,
            RiskLevel = riskLevel,
            Priority = priority,
            ApprovalStrategy = EnumNameHelper.TryParseName<ApprovalStrategyType>(
                escalationConfig.DefaultApprovalStrategy, out var strategy)
                ? strategy : ApprovalStrategyType.AnyOf,
            Approvers = approvers,
            QuorumThreshold = 1,
            TimeoutSeconds = escalationConfig.DefaultTimeoutSeconds,
            TimeoutAction = EnumNameHelper.TryParseName<EscalationTimeoutAction>(
                escalationConfig.DefaultTimeoutAction, out var timeoutAction)
                ? timeoutAction : fallbackTimeoutAction,
            RequestedAt = DateTimeOffset.UtcNow,
            EscalationTierTarget = escalationTierTarget,
            PredecessorEscalationId = predecessorEscalationId
        };

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
            WarnOnceGate.WarnOnce(ref s_delegationApproversWarned, () => _logger.LogWarning(
                "AppConfig:AI:Governance:Escalation:DelegationApprovers is empty — a delegation " +
                "blocked by autonomy tier can never be escalated for approval and is denied " +
                "(fail-closed). Configure at least one approver to enable delegation escalation."));

            return null;
        }

        // #394: EscalationTierTarget records the tier THIS delegation actually needed
        // (minimumTier), not a fixed constant — if this escalation times out under
        // Escalate/DenyAndEscalate, it resolves as a real tier hand-off instead of a bare denial.
        // HandleEscalatedTierAsync below is the caller-owned downstream process that raises the
        // tier-2 escalation; approving it does not grant minimumTier itself, it only unblocks the
        // retry the same way an ordinary tier-1 approval does (see HandleEscalatedTierAsync's
        // remarks).
        var escalationRequest = BuildDelegationEscalationRequest(
            requiredCapabilities,
            new Dictionary<string, string>
            {
                ["taskDescription"] = taskDescription,
                ["minimumTier"] = minimumTier.ToString()
            },
            $"Delegation blocked by autonomy tier ({minimumTier}): {taskDescription}",
            RiskLevel.Medium,
            EscalationPriority.Blocking,
            roster,
            EscalationTimeoutAction.DenyAndEscalate,
            escalationConfig,
            escalationTierTarget: minimumTier);

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
            WarnOnceGate.WarnOnce(ref s_delegationEscalatedApproversWarned, () => _logger.LogWarning(
                "Escalation {EscalationId} was escalated to tier {Tier} but " +
                "AppConfig:AI:Governance:Escalation:DelegationEscalatedApprovers is empty — " +
                "denying (fail-closed). Configure at least one escalated approver to enable it.",
                tier1Outcome.EscalationId, tier1Outcome.EscalatedToTier));

            return null;
        }

        // Same config-driven parsing as the tier-1 request — a configured approval strategy or
        // timeout action should apply uniformly to both tiers, not silently weaken at the more
        // sensitive, higher-priority tier. TimeoutAction is deliberately never itself escalatable
        // here: EscalationTierTarget is left unset on this request, so even a configured
        // Escalate/DenyAndEscalate action resolves as an ordinary timeout denial, never a third
        // escalation tier.
        var tier2Request = BuildDelegationEscalationRequest(
            requiredCapabilities,
            new Dictionary<string, string> { ["taskDescription"] = taskDescription },
            $"Escalated (autonomy tier {tier1Outcome.EscalatedToTier} unmet, first tier " +
            $"unanswered): {taskDescription}",
            RiskLevel.High,
            EscalationPriority.Critical,
            escalatedRoster,
            EscalationTimeoutAction.Deny,
            escalationConfig,
            predecessorEscalationId: tier1Outcome.EscalationId);

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
            // Full detail stays in the structured log only — ex.Message can carry a secret (connection
            // string, SAS token), and RecordFailure/DelegationResult.Fail both write into surfaces that
            // reach the audit trail and, via DelegateToSubagentTool.cs's ToolResult.Fail(result.FailureReason
            // ?? ...), a governed tool's reported failure text. Matches the ex.GetType().Name-only
            // convention MediatorDispatchRunner/WorkspaceCommandRunner already use.
            _logger.LogError(ex, "Delegation {DelegationId} to {AgentId} failed",
                delegationId, selection.SelectedAgent.AgentId);
            var reason = SafeFailureText.For("Delegation failed", ex);
            await RecordFailure(pendingRecord, reason, ct);
            return DelegationResult.Fail(reason);
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
        // #518: a named-agent delegation (SubagentType.NamedAgent) has no ISubagentProfileRegistry
        // entry — GetProfile only knows the built-in profiles. Build the runnable agent the same way
        // an ordinary turn does for an AGENT.md-registered agent (skill resolution, full context
        // provider rail — including PeerAgentContextProvider, so a delegated agent sees ITS OWN
        // peers too) rather than the profile path's lightweight, skill-free CreateFromDelegation.
        AIAgent agent;
        if (selection.SelectedAgent.AgentType == SubagentType.NamedAgent)
        {
            var target = _agentRegistry.TryGet(selection.SelectedAgent.AgentId)
                ?? throw new InvalidOperationException(
                    $"Named delegation target '{selection.SelectedAgent.AgentId}' was validated at "
                    + "selection time but is no longer registered.");
            var skillIds = target.Skills is { Count: > 0 } ? target.Skills : [target.Id];
            var options = new SkillAgentOptions
            {
                AgentNameOverride = target.Id,
                OwningAgentId = target.Id,
                AgentInstructions = target.Instructions,
                // #518 correctness-review finding: omitting this let a named delegation bypass the
                // target's own AGENT.md tool ceiling entirely — ExecuteAgentTurnCommandHandler's
                // ordinary-turn path this branch claims to mirror always passes it
                // (AllowedTools = agentDef?.AllowedTools). AgentDefinition.AllowedTools is empty, not
                // null, when the agent declares no ceiling, so this assignment is a direct 1:1 mapping
                // of the same "empty means unrestricted" contract that path already relies on.
                AllowedTools = target.AllowedTools
            };
            var built = await _agentFactory.CreateAgentWithContextFromSkillsAsync(skillIds, options, ct);
            agent = built.Agent;
        }
        else
        {
            var definition = _profileRegistry.GetProfile(selection.SelectedAgent.AgentType);
            var agentContext = _contextFactory.CreateFromDelegation(definition, toolOverrides, currentDepth + 1, pendingRecord.DelegationId);
            agent = await _agentFactory.CreateAgentAsync(agentContext, ct);
        }

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
