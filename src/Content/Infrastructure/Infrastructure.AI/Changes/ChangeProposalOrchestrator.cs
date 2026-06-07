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

        var outcome = await RunGatesAsync(
            proposal,
            validationGates,
            selfLoopOnDefer: ChangeProposalStatus.Validating,
            mode,
            correlationId,
            maxDefers,
            cancellationToken).ConfigureAwait(false);
        if (outcome.Kind != PhaseOutcomeKind.Completed)
        {
            return outcome.Proposal;
        }
        proposal = outcome.Proposal;

        // Validation phase complete. The transition depends on whether the
        // proposal carries an approval gate:
        //  - Has approval gate: invoke it; gate returns Pass / Fail / Defer.
        //    Defer → transition to AwaitingApproval (wait for human via the
        //    Approve/Reject CQRS commands). Pass → synthetic transit through
        //    AwaitingApproval to Approved (auto-approve under autonomy tier).
        //    Fail → terminal Rejected.
        //  - No approval gate (e.g. Trivial blast radius): transit through
        //    AwaitingApproval to Approved with a synthetic "auto-approver"
        //    audit entry so the trail records exactly what happened.
        if (hasApproval)
        {
            var attempt = ConsecutiveDeferAttempts(proposal, WellKnownGateKeys.Approval) + 1;
            if (attempt > maxDefers)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    new GateDecision
                    {
                        Timestamp = _time.GetUtcNow(),
                        GateKey = WellKnownGateKeys.Approval,
                        Action = GateAction.Fail,
                        Reason = $"approval defer budget exhausted ({maxDefers} consecutive Defers).",
                        DurationMs = 0
                    },
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            var (approvalDecision, terminate) = await EvaluateGateAsync(
                proposal, WellKnownGateKeys.Approval, mode, attempt, correlationId, cancellationToken)
                .ConfigureAwait(false);

            if (terminate)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    approvalDecision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (approvalDecision.Action == GateAction.Defer)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.AwaitingApproval,
                    approvalDecision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            // Pass — gate auto-approved (e.g. autonomy tier ruling under PR-4).
            // Route through AwaitingApproval to Approved per the state machine.
            proposal = await TransitionAsync(
                proposal,
                ChangeProposalStatus.AwaitingApproval,
                approvalDecision with { Reason = $"approval gate auto-approved: {approvalDecision.Reason}" },
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);

            proposal = await TransitionAsync(
                proposal,
                ChangeProposalStatus.Approved,
                approvalDecision,
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);

            return proposal;
        }

        // No approval gate in RequiredGates — auto-approve by omission.
        proposal = await TransitionAsync(
            proposal,
            ChangeProposalStatus.AwaitingApproval,
            new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = "orchestrator",
                Action = GateAction.Pass,
                Reason = "validation phase passed, awaiting auto-approval",
                DurationMs = 0
            },
            mode,
            correlationId,
            cancellationToken).ConfigureAwait(false);

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

        // Merging does not legally self-loop in the state machine, so a Defer
        // records history without a status transition (selfLoopOnDefer: null).
        var outcome = await RunGatesAsync(
            proposal,
            mergeGates,
            selfLoopOnDefer: null,
            mode,
            correlationId,
            maxDefers,
            cancellationToken).ConfigureAwait(false);
        if (outcome.Kind != PhaseOutcomeKind.Completed)
        {
            return outcome.Proposal;
        }
        proposal = outcome.Proposal;

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

    /// <summary>
    /// Iterate a sequence of gate keys, resuming from the first not-yet-passed
    /// gate. Returns one of three outcomes:
    /// <list type="bullet">
    ///   <item><description><see cref="PhaseOutcomeKind.Completed"/> — all gates Passed; caller runs the phase-specific terminal transition.</description></item>
    ///   <item><description><see cref="PhaseOutcomeKind.Paused"/> — a gate returned <c>Defer</c>; the proposal is saved (transitioned per <paramref name="selfLoopOnDefer"/> or history-only) and the caller returns it as-is for an upstream retry.</description></item>
    ///   <item><description><see cref="PhaseOutcomeKind.Terminated"/> — a gate returned <c>Fail</c> or defer budget was exhausted; the proposal is transitioned to <c>Rejected</c>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="selfLoopOnDefer">
    /// When non-null, a Defer causes a TransitionTo to this status (the validation
    /// phase passes <see cref="ChangeProposalStatus.Validating"/> to record the
    /// legal self-loop). When null, a Defer appends history without transition
    /// (the merge phase has no legal self-loop on <see cref="ChangeProposalStatus.Merging"/>).
    /// </param>
    private async Task<PhaseOutcome> RunGatesAsync(
        ChangeProposal proposal,
        IReadOnlyList<string> gates,
        ChangeProposalStatus? selfLoopOnDefer,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        var startIndex = CompletedGateCount(proposal, gates);
        for (var i = startIndex; i < gates.Count; i++)
        {
            var gateKey = gates[i];
            var attempt = ConsecutiveDeferAttempts(proposal, gateKey) + 1;
            if (attempt > maxDefers)
            {
                var rejected = await TransitionAsync(
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
                return new PhaseOutcome(PhaseOutcomeKind.Terminated, rejected);
            }

            var (decision, terminate) = await EvaluateGateAsync(proposal, gateKey, mode, attempt, correlationId, cancellationToken)
                .ConfigureAwait(false);

            if (terminate)
            {
                var rejected = await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    decision,
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
                return new PhaseOutcome(PhaseOutcomeKind.Terminated, rejected);
            }

            if (decision.Action == GateAction.Defer)
            {
                var deferred = selfLoopOnDefer.HasValue
                    ? await TransitionAsync(proposal, selfLoopOnDefer.Value, decision, mode, correlationId, cancellationToken).ConfigureAwait(false)
                    : await AppendHistoryAndSaveAsync(proposal, decision, mode, correlationId, cancellationToken).ConfigureAwait(false);
                return new PhaseOutcome(PhaseOutcomeKind.Paused, deferred);
            }

            proposal = await AppendHistoryAndSaveAsync(proposal, decision, mode, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PhaseOutcome(PhaseOutcomeKind.Completed, proposal);
    }

    private enum PhaseOutcomeKind
    {
        /// <summary>Every gate in the sequence returned Pass.</summary>
        Completed,
        /// <summary>A gate returned Defer; the proposal is paused for retry.</summary>
        Paused,
        /// <summary>A gate Failed or the defer budget was exhausted; the proposal was transitioned to Rejected.</summary>
        Terminated
    }

    private readonly record struct PhaseOutcome(PhaseOutcomeKind Kind, ChangeProposal Proposal);

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
