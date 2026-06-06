using System.Diagnostics;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Sequential gate-pipeline orchestrator. Picks up a proposal at its current
/// status and pushes it as far as it can go without external intervention,
/// pausing at <c>AwaitingApproval</c> for human approval and at any
/// <c>GateAction.Defer</c> for an upstream retry.
/// </summary>
/// <remarks>
/// <para>
/// State-machine flow:
/// <list type="bullet">
///   <item><description><c>Draft</c> → <c>Validating</c>: orchestrator picks up, runs validation phase (every gate before <c>approval</c>).</description></item>
///   <item><description><c>Validating</c> → <c>AwaitingApproval</c>: all validation gates Pass and the pipeline includes an <c>approval</c> gate.</description></item>
///   <item><description><c>Validating</c> → <c>Approved</c>: all validation gates Pass and the pipeline omits <c>approval</c> (auto-approve under autonomous tier / trivial radius).</description></item>
///   <item><description><c>Approved</c> → <c>Merging</c> → <c>Merged</c>: orchestrator picks up, runs every gate after <c>approval</c> (typically just <c>merge</c>).</description></item>
///   <item><description>Any gate Fail at any phase → <c>Rejected</c>.</description></item>
///   <item><description>Any gate Defer → proposal stays in current status; orchestrator returns and the caller schedules retry.</description></item>
///   <item><description>Gate exception → <c>Rejected</c> with the exception type recorded; never partial-merge.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ChangeProposalOrchestrator : IChangeProposalOrchestrator
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeAuditWriter _audit;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ChangeProposalOrchestrator> _logger;

    /// <summary>Initializes a new <see cref="ChangeProposalOrchestrator"/>.</summary>
    public ChangeProposalOrchestrator(
        IChangeProposalStore store,
        IChangeAuditWriter audit,
        IServiceProvider services,
        TimeProvider time,
        IOptionsMonitor<AppConfig> config,
        ILogger<ChangeProposalOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _audit = audit;
        _services = services;
        _time = time;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChangeProposal?> ProcessAsync(
        string proposalId,
        OrchestratorMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);

        var proposal = await _store.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            _logger.LogWarning("ChangeProposal {ProposalId} not found during orchestration.", proposalId);
            return null;
        }

        if (proposal.IsTerminal || proposal.Status == ChangeProposalStatus.AwaitingApproval)
        {
            return proposal;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var maxDefers = Math.Max(1, _config.CurrentValue.AI.Changes.MaxConsecutiveDefers);

        proposal = await AdvanceFromCurrentStatusAsync(proposal, mode, correlationId, maxDefers, cancellationToken)
            .ConfigureAwait(false);
        return proposal;
    }

    private async Task<ChangeProposal> AdvanceFromCurrentStatusAsync(
        ChangeProposal proposal,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        // Draft is transient — flip to Validating immediately so the audit/state
        // machine record the moment the orchestrator picked the work up.
        if (proposal.Status == ChangeProposalStatus.Draft)
        {
            proposal = await TransitionAsync(
                proposal,
                ChangeProposalStatus.Validating,
                new GateDecision
                {
                    Timestamp = _time.GetUtcNow(),
                    GateKey = "orchestrator",
                    Action = GateAction.Pass,
                    Reason = "orchestrator picked up Draft",
                    DurationMs = 0
                },
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        if (proposal.Status == ChangeProposalStatus.Validating)
        {
            proposal = await RunValidationPhaseAsync(proposal, mode, correlationId, maxDefers, cancellationToken)
                .ConfigureAwait(false);
        }

        // Approved is transient — flip to Merging then run the merge phase.
        if (proposal.Status == ChangeProposalStatus.Approved)
        {
            proposal = await TransitionAsync(
                proposal,
                ChangeProposalStatus.Merging,
                new GateDecision
                {
                    Timestamp = _time.GetUtcNow(),
                    GateKey = "orchestrator",
                    Action = GateAction.Pass,
                    Reason = "orchestrator picked up Approved",
                    DurationMs = 0
                },
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        if (proposal.Status == ChangeProposalStatus.Merging)
        {
            proposal = await RunMergePhaseAsync(proposal, mode, correlationId, maxDefers, cancellationToken)
                .ConfigureAwait(false);
        }

        return proposal;
    }

    private async Task<ChangeProposal> RunValidationPhaseAsync(
        ChangeProposal proposal,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        var validationGates = ValidationGates(proposal.RequiredGates);
        var hasApproval = proposal.RequiredGates.Contains(WellKnownGateKeys.Approval, StringComparer.Ordinal);

        // Skip gates already completed in this status (resumption after a Defer).
        var startIndex = CompletedGateCount(proposal, validationGates);
        for (var i = startIndex; i < validationGates.Count; i++)
        {
            var gateKey = validationGates[i];
            var attempt = ConsecutiveDeferAttempts(proposal, gateKey) + 1;
            if (attempt > maxDefers)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    new GateDecision
                    {
                        Timestamp = _time.GetUtcNow(),
                        GateKey = gateKey,
                        Action = GateAction.Fail,
                        Reason = $"defer budget exhausted ({maxDefers} consecutive Defers).",
                        DurationMs = 0
                    },
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            var (decision, terminate) = await EvaluateGateAsync(proposal, gateKey, mode, attempt, correlationId, cancellationToken)
                .ConfigureAwait(false);

            if (terminate)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    decision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (decision.Action == GateAction.Defer)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Validating,  // self-loop
                    decision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            // Pass — record but don't transition yet; we batch the validation
            // transition at the end of the phase.
            proposal = await AppendHistoryAndSaveAsync(proposal, decision, mode, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Validation phase complete. Always route through AwaitingApproval so
        // the state machine stays linear (Validating → AwaitingApproval → Approved).
        // For the auto-approve case (no approval gate in RequiredGates), the
        // orchestrator immediately follows up with a synthetic auto-approval
        // decision so the audit trail records exactly what happened —
        // distinguishable from a real human nod by the reviewer id "auto-approver".
        proposal = await TransitionAsync(
            proposal,
            ChangeProposalStatus.AwaitingApproval,
            new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = "orchestrator",
                Action = GateAction.Pass,
                Reason = hasApproval
                    ? "validation phase passed, awaiting approval"
                    : "validation phase passed, awaiting auto-approval",
                DurationMs = 0
            },
            mode,
            correlationId,
            cancellationToken).ConfigureAwait(false);

        if (!hasApproval)
        {
            proposal = await TransitionAsync(
                proposal,
                ChangeProposalStatus.Approved,
                new GateDecision
                {
                    Timestamp = _time.GetUtcNow(),
                    GateKey = WellKnownGateKeys.Approval,
                    Action = GateAction.Pass,
                    Reason = "auto-approved (no approval gate in RequiredGates)",
                    ReviewerId = "auto-approver",
                    DurationMs = 0
                },
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        return proposal;
    }

    private async Task<ChangeProposal> RunMergePhaseAsync(
        ChangeProposal proposal,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        var mergeGates = MergeGates(proposal.RequiredGates);

        var startIndex = CompletedGateCount(proposal, mergeGates);
        for (var i = startIndex; i < mergeGates.Count; i++)
        {
            var gateKey = mergeGates[i];
            var attempt = ConsecutiveDeferAttempts(proposal, gateKey) + 1;
            if (attempt > maxDefers)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    new GateDecision
                    {
                        Timestamp = _time.GetUtcNow(),
                        GateKey = gateKey,
                        Action = GateAction.Fail,
                        Reason = $"defer budget exhausted ({maxDefers} consecutive Defers).",
                        DurationMs = 0
                    },
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            var (decision, terminate) = await EvaluateGateAsync(proposal, gateKey, mode, attempt, correlationId, cancellationToken)
                .ConfigureAwait(false);

            if (terminate)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    decision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (decision.Action == GateAction.Defer)
            {
                // Note: Merging does not legally self-loop in the state machine;
                // record the defer at the current status without transition by
                // appending history only. Caller schedules retry.
                proposal = await AppendHistoryAndSaveAsync(proposal, decision, mode, correlationId, cancellationToken)
                    .ConfigureAwait(false);
                return proposal;
            }

            proposal = await AppendHistoryAndSaveAsync(proposal, decision, mode, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return await TransitionAsync(
            proposal,
            ChangeProposalStatus.Merged,
            new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = "orchestrator",
                Action = GateAction.Pass,
                Reason = mode == OrchestratorMode.Shadow
                    ? "merge phase complete (shadow — no real effect)"
                    : "merge phase complete",
                DurationMs = 0
            },
            mode,
            correlationId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(GateDecision Decision, bool Terminate)> EvaluateGateAsync(
        ChangeProposal proposal,
        string gateKey,
        OrchestratorMode mode,
        int attempt,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var gate = _services.GetKeyedService<IChangeProposalGate>(gateKey);
        if (gate is null)
        {
            var decision = new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = gateKey,
                Action = GateAction.Fail,
                Reason = $"No IChangeProposalGate registered for key '{gateKey}'.",
                DurationMs = 0
            };
            return (decision, Terminate: true);
        }

        var context = new GateContext
        {
            Mode = mode,
            AttemptCount = attempt,
            EvaluatedAt = _time.GetUtcNow(),
            CorrelationId = correlationId
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await gate.EvaluateAsync(proposal, context, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var decision = new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = gateKey,
                Action = result.Action,
                Reason = result.Reason,
                EvidenceHash = result.EvidenceHash,
                DurationMs = sw.ElapsedMilliseconds
            };
            return (decision, Terminate: result.Action == GateAction.Fail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(
                ex,
                "Gate '{GateKey}' threw evaluating proposal {ProposalId} (correlation {CorrelationId}).",
                gateKey,
                proposal.Id,
                correlationId);
            var decision = new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = gateKey,
                Action = GateAction.Fail,
                Reason = $"Gate threw: {ex.GetType().Name}: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
            return (decision, Terminate: true);
        }
    }

    private async Task<ChangeProposal> TransitionAsync(
        ChangeProposal proposal,
        ChangeProposalStatus next,
        GateDecision decision,
        OrchestratorMode mode,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _audit.AppendAsync(proposal, decision, proposal.SubmittedBy, mode, correlationId, cancellationToken)
            .ConfigureAwait(false);
        var transitioned = proposal.TransitionTo(next, decision);
        await _store.SaveAsync(transitioned, cancellationToken).ConfigureAwait(false);
        return transitioned;
    }

    private async Task<ChangeProposal> AppendHistoryAndSaveAsync(
        ChangeProposal proposal,
        GateDecision decision,
        OrchestratorMode mode,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _audit.AppendAsync(proposal, decision, proposal.SubmittedBy, mode, correlationId, cancellationToken)
            .ConfigureAwait(false);
        // History append without status transition — use the existing self-loop
        // transition via TransitionTo with the same status (state machine allows
        // Validating→Validating; Merging is handled differently above).
        var withHistory = proposal with
        {
            History = [.. proposal.History, decision]
        };
        await _store.SaveAsync(withHistory, cancellationToken).ConfigureAwait(false);
        return withHistory;
    }

    private static IReadOnlyList<string> ValidationGates(IReadOnlyList<string> required) =>
        required
            .TakeWhile(k => !string.Equals(k, WellKnownGateKeys.Approval, StringComparison.Ordinal))
            .ToList();

    private static IReadOnlyList<string> MergeGates(IReadOnlyList<string> required)
    {
        var afterApproval = required
            .SkipWhile(k => !string.Equals(k, WellKnownGateKeys.Approval, StringComparison.Ordinal))
            .Skip(1)  // skip the approval gate itself
            .ToList();
        return afterApproval.Count > 0
            ? afterApproval
            // No approval gate in the pipeline → everything after validation gates is the merge phase.
            : required
                .SkipWhile(k => string.Equals(k, WellKnownGateKeys.SelfValidation, StringComparison.Ordinal)
                             || string.Equals(k, WellKnownGateKeys.Policy, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>
    /// Count how many gates from <paramref name="gates"/> have already produced
    /// a Pass decision in <paramref name="proposal"/>'s history during this
    /// phase. Used to resume after a Defer without re-running already-passed gates.
    /// </summary>
    private static int CompletedGateCount(ChangeProposal proposal, IReadOnlyList<string> gates)
    {
        var count = 0;
        for (var i = 0; i < gates.Count; i++)
        {
            var gateKey = gates[i];
            var passed = proposal.History.Any(h =>
                string.Equals(h.GateKey, gateKey, StringComparison.Ordinal) &&
                h.Action == GateAction.Pass);
            if (!passed)
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static int ConsecutiveDeferAttempts(ChangeProposal proposal, string gateKey)
    {
        var count = 0;
        for (var i = proposal.History.Count - 1; i >= 0; i--)
        {
            var entry = proposal.History[i];
            if (!string.Equals(entry.GateKey, gateKey, StringComparison.Ordinal))
            {
                continue;
            }
            if (entry.Action != GateAction.Defer)
            {
                break;
            }
            count++;
        }
        return count;
    }
}
