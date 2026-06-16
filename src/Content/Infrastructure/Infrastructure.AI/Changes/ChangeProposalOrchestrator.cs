using System.Diagnostics;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.IncidentResponse;
using Domain.AI.Changes;
using Domain.Common.Config;
using Domain.Common.Config.AI.IncidentResponse;
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
    private readonly IIncidentContext? _incidentContext;
    private readonly IIncidentResponsePlanResolver? _incidentResolver;

    /// <summary>
    /// Stable, scrubbed reason code recorded when a gate's
    /// <c>EvaluateAsync</c> throws. Used in place of the raw exception message so
    /// that audit/history sinks never persist credentials embedded in exception
    /// text (e.g. SAS tokens in <see cref="System.Net.Http.HttpRequestException"/>
    /// URLs). The full exception is always available via structured logs.
    /// </summary>
    internal const string GateExceptionReasonCode = "gate_threw";

    /// <summary>Initializes a new <see cref="ChangeProposalOrchestrator"/>.</summary>
    /// <param name="store">Proposal persistence.</param>
    /// <param name="audit">Append-only audit sink for gate decisions.</param>
    /// <param name="services">Service provider used to resolve gates by key.</param>
    /// <param name="time">Time provider for audit timestamps.</param>
    /// <param name="config">Application configuration (defer budget, etc.).</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="incidentContext">
    /// Optional (PR-5) — when supplied alongside <paramref name="incidentResolver"/>,
    /// the orchestrator overlays any active incident plan's
    /// <see cref="IncidentResponsePlan.AdditionalRequiredGates"/> on every
    /// proposal it processes. Null in tests that exercise the orchestrator in
    /// isolation; non-null when registered through DI.
    /// </param>
    /// <param name="incidentResolver">
    /// Optional (PR-5) — resolves the incident type carried by
    /// <paramref name="incidentContext"/> to the active plan. Null in tests
    /// that exercise the orchestrator in isolation; non-null when registered
    /// through DI.
    /// </param>
    public ChangeProposalOrchestrator(
        IChangeProposalStore store,
        IChangeAuditWriter audit,
        IServiceProvider services,
        TimeProvider time,
        IOptionsMonitor<AppConfig> config,
        ILogger<ChangeProposalOrchestrator> logger,
        IIncidentContext? incidentContext = null,
        IIncidentResponsePlanResolver? incidentResolver = null)
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
        _incidentContext = incidentContext;
        _incidentResolver = incidentResolver;
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

        // PR-5: compute the effective required gates *once* per ProcessAsync
        // invocation. The proposal's stored RequiredGates is never mutated;
        // the orchestrator evaluates against the overlay at runtime so the
        // audit trail records both the original shape and the incident-driven
        // additions. The overlay marker is emitted before the first phase
        // transition so reviewers see the augment alongside the orchestrator's
        // "picked up Draft" line.
        var (effectiveGates, activePlan) = ResolveEffectiveGates(proposal);
        if (activePlan is not null && effectiveGates.Count != proposal.RequiredGates.Count)
        {
            proposal = await AppendHistoryAndSaveAsync(
                proposal,
                new GateDecision
                {
                    Timestamp = _time.GetUtcNow(),
                    GateKey = "incident_overlay",
                    Action = GateAction.Pass,
                    Reason = $"incident plan '{activePlan.Name}' overlaid gates: " +
                             string.Join(",", activePlan.AdditionalRequiredGates),
                    DurationMs = 0
                },
                mode,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }

        proposal = await AdvanceFromCurrentStatusAsync(proposal, effectiveGates, mode, correlationId, maxDefers, cancellationToken)
            .ConfigureAwait(false);
        return proposal;
    }

    /// <summary>
    /// Compute the effective required-gate list for this orchestrator run by
    /// overlaying any active incident plan's
    /// <see cref="IncidentResponsePlan.AdditionalRequiredGates"/> on the
    /// proposal's stored <c>RequiredGates</c>. Returns the proposal's original
    /// list (and a null plan) when no incident is active, the resolver is not
    /// registered, or the matching plan has no additional gates.
    /// </summary>
    /// <remarks>
    /// Order preservation: the proposal's original gates come first in their
    /// declared order; incident-added gates that are NOT already present in
    /// the proposal's list are appended in the plan's declared order. Gates
    /// already in the proposal are silently de-duplicated — the orchestrator
    /// never runs the same gate twice in one phase.
    /// </remarks>
    private (IReadOnlyList<string> EffectiveGates, IncidentResponsePlan? Plan) ResolveEffectiveGates(
        ChangeProposal proposal)
    {
        if (_incidentContext is null || _incidentResolver is null)
        {
            return (proposal.RequiredGates, null);
        }

        var incidentType = _incidentContext.CurrentIncidentType;
        var plan = _incidentResolver.ResolveFor(incidentType);
        if (plan is null || plan.AdditionalRequiredGates.Count == 0)
        {
            return (proposal.RequiredGates, plan);
        }

        var existing = new HashSet<string>(proposal.RequiredGates, StringComparer.Ordinal);
        var effective = new List<string>(proposal.RequiredGates.Count + plan.AdditionalRequiredGates.Count);
        effective.AddRange(proposal.RequiredGates);
        for (var i = 0; i < plan.AdditionalRequiredGates.Count; i++)
        {
            var extra = plan.AdditionalRequiredGates[i];
            if (existing.Add(extra))
            {
                effective.Add(extra);
            }
        }
        return (effective, plan);
    }

    private async Task<ChangeProposal> AdvanceFromCurrentStatusAsync(
        ChangeProposal proposal,
        IReadOnlyList<string> effectiveGates,
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
            proposal = await RunValidationPhaseAsync(proposal, effectiveGates, mode, correlationId, maxDefers, cancellationToken)
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
            proposal = await RunMergePhaseAsync(proposal, effectiveGates, mode, correlationId, maxDefers, cancellationToken)
                .ConfigureAwait(false);
        }

        return proposal;
    }

    private async Task<ChangeProposal> RunValidationPhaseAsync(
        ChangeProposal proposal,
        IReadOnlyList<string> effectiveGates,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        var validationGates = GatesForPhase(effectiveGates, GatePhase.Validation);
        var approvalGateKey = FindApprovalGateKey(effectiveGates);

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
        // proposal carries an approval-phase gate:
        //  - Has approval gate: invoke it; gate returns Pass / Fail / Defer.
        //    Defer → transition to AwaitingApproval (wait for human via the
        //    Approve/Reject CQRS commands). Pass → synthetic transit through
        //    AwaitingApproval to Approved (auto-approve under autonomy tier).
        //    Fail → terminal Rejected.
        //  - No approval gate (e.g. Trivial blast radius): transit through
        //    AwaitingApproval to Approved with a synthetic "auto-approver"
        //    audit entry so the trail records exactly what happened.
        if (approvalGateKey is not null)
        {
            var attempt = ConsecutiveDeferAttempts(proposal, approvalGateKey) + 1;
            if (attempt > maxDefers)
            {
                return await TransitionAsync(
                    proposal,
                    ChangeProposalStatus.Rejected,
                    new GateDecision
                    {
                        Timestamp = _time.GetUtcNow(),
                        GateKey = approvalGateKey,
                        Action = GateAction.Fail,
                        Reason = $"approval defer budget exhausted ({maxDefers} consecutive Defers).",
                        DurationMs = 0
                    },
                    mode,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
            }

            var (approvalDecision, terminate) = await EvaluateGateAsync(
                proposal, approvalGateKey, mode, attempt, correlationId, cancellationToken)
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
        IReadOnlyList<string> effectiveGates,
        OrchestratorMode mode,
        string correlationId,
        int maxDefers,
        CancellationToken cancellationToken)
    {
        var mergeGates = GatesForPhase(effectiveGates, GatePhase.Merge);

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
                // Persist a stable scrubbed code plus the exception *type* only — never
                // ex.Message. This Reason is written to the changes.jsonl audit file and
                // appended to proposal History (returned to callers via store/CQRS queries).
                // Gates are extension points that call HTTP services and external tooling;
                // their exception messages routinely embed full request URLs with SAS
                // tokens or query-string credentials. The full exception is captured above
                // via structured logging (correlatable by ProposalId/CorrelationId).
                Reason = $"{GateExceptionReasonCode}: {ex.GetType().Name}",
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

    /// <summary>
    /// Filter <paramref name="required"/> to keys whose registered gate declares
    /// <paramref name="phase"/>. Order is preserved from <paramref name="required"/>
    /// so gates within a phase run in the order the proposal specified.
    /// </summary>
    /// <remarks>
    /// A key with no registered gate is treated as <see cref="GatePhase.Validation"/>
    /// so the "no gate registered for key" failure surfaces during validation rather
    /// than after a synthetic auto-approval. The orchestrator's
    /// <see cref="EvaluateGateAsync"/> Fail path then transitions the proposal to
    /// Rejected with a directive message — same behaviour as before phase
    /// metadata existed.
    /// </remarks>
    private IReadOnlyList<string> GatesForPhase(IReadOnlyList<string> required, GatePhase phase)
    {
        var result = new List<string>(required.Count);
        for (var i = 0; i < required.Count; i++)
        {
            var key = required[i];
            if (ResolvePhase(key) == phase)
            {
                result.Add(key);
            }
        }
        return result;
    }

    /// <summary>
    /// Return the first key in <paramref name="required"/> whose registered gate
    /// declares <see cref="GatePhase.Approval"/>, or null if the proposal carries
    /// no approval-phase gate. The orchestrator uses the null case to trigger
    /// auto-approve-by-omission (typically for Trivial blast radius proposals).
    /// </summary>
    /// <remarks>
    /// "First approval-phase gate" — a quorum-style multi-approval setup that
    /// registered two approval-phase gates would have only the first one invoked.
    /// Future work if a real consumer needs quorum: extend the orchestrator to
    /// iterate all approval-phase gates and aggregate their results.
    /// </remarks>
    private string? FindApprovalGateKey(IReadOnlyList<string> required)
    {
        for (var i = 0; i < required.Count; i++)
        {
            if (ResolvePhase(required[i]) == GatePhase.Approval)
            {
                return required[i];
            }
        }
        return null;
    }

    private GatePhase ResolvePhase(string gateKey)
    {
        var gate = _services.GetKeyedService<IChangeProposalGate>(gateKey);
        return gate?.Phase ?? GatePhase.Validation;
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
