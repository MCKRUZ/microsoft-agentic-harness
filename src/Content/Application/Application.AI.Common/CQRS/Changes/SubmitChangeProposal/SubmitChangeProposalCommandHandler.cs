using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Agents;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.SubmitChangeProposal;

/// <summary>
/// Handles <see cref="SubmitChangeProposalCommand"/>: resolves required gates,
/// derives the deterministic id, persists the proposal in Draft, and enqueues
/// it on the <see cref="IChangeProposalDispatchQueue"/> for asynchronous
/// orchestrator dispatch. Returns the Draft snapshot immediately — the caller
/// polls <c>GetChangeProposalQueryHandler</c> (or subscribes to a future
/// outcome notification stream) for the post-pipeline state. Idempotent: a
/// duplicate submission within the same id-bucket returns the prior proposal
/// verbatim without re-enqueueing.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour change from inline-orchestrator: the command no longer blocks
/// until the pipeline reaches a quiescent status. A 30-second policy gate
/// and a 20-second merge gate used to keep the HTTP request open for ~50
/// seconds; behind a load-balancer with a tight idle timeout, the response
/// dropped while the orchestrator finished server-side and the caller had
/// no way to learn the outcome. The dispatcher decouples the request from
/// the pipeline's wall-clock cost.
/// </para>
/// <para>
/// Crash semantics. Save-then-Enqueue: a host crash between the two leaves
/// the proposal at Draft on disk; a re-Submit hits the idempotency check
/// and re-enqueues. With the default in-memory dispatch queue, ids enqueued
/// before a crash are lost — same loss profile as the default in-memory
/// store, which the startup validator forces consumers to explicitly opt
/// in to outside Development. Consumers requiring at-least-once delivery
/// wire an outbox-backed <see cref="IChangeProposalDispatchQueue"/>; the
/// seam is here.
/// </para>
/// </remarks>
public sealed class SubmitChangeProposalCommandHandler
    : IRequestHandler<SubmitChangeProposalCommand, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalGateResolver _gateResolver;
    private readonly IChangeProposalDispatchQueue _dispatchQueue;
    private readonly IAgentExecutionContext _agentContext;
    private readonly IAutonomyDecisionEvaluator? _autonomyEvaluator;
    private readonly IAutonomyTierResolver? _tierResolver;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SubmitChangeProposalCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="SubmitChangeProposalCommandHandler"/>.</summary>
    /// <remarks>
    /// <paramref name="autonomyEvaluator"/> and <paramref name="tierResolver"/> are
    /// optional (PR-4 graded autonomy). When either is missing the handler skips
    /// the per-blast-radius decision computation and falls back to the gate
    /// resolver's pre-PR-4 behaviour — preserving PR-2's contract for consumers
    /// who haven't wired the new evaluator.
    /// </remarks>
    public SubmitChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalGateResolver gateResolver,
        IChangeProposalDispatchQueue dispatchQueue,
        IAgentExecutionContext agentContext,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<SubmitChangeProposalCommandHandler> logger,
        IAutonomyDecisionEvaluator? autonomyEvaluator = null,
        IAutonomyTierResolver? tierResolver = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateResolver);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        ArgumentNullException.ThrowIfNull(agentContext);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _gateResolver = gateResolver;
        _dispatchQueue = dispatchQueue;
        _agentContext = agentContext;
        _autonomyEvaluator = autonomyEvaluator;
        _tierResolver = tierResolver;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        SubmitChangeProposalCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var changesConfig = _config.CurrentValue.AI.Changes;
        if (!changesConfig.Enabled)
        {
            return Result<ChangeProposal>.Forbidden(
                "ChangeProposal pipeline is disabled. Set AppConfig.AI.Changes.Enabled = true to enable.");
        }

        var identity = _agentContext.AgentIdentity;
        if (identity is null)
        {
            return Result<ChangeProposal>.Unauthorized(
                "SubmitChangeProposalCommand requires an ambient agent identity. " +
                "Caller must execute inside an agent scope established by AgentIdentityResolutionBehavior.");
        }

        var submittedAt = request.SubmittedAt ?? _time.GetUtcNow();

        // PR-4 graded autonomy: compute the per-blast-radius decision when the
        // evaluator + tier resolver are wired and the caller hasn't supplied an
        // explicit RequiredGates override. The decision flows into the resolver
        // and decides whether the approval gate appears in the frozen gate list.
        AutonomyDecisionResult? decision = null;
        if (request.RequiredGates is null
            && _autonomyEvaluator is not null
            && _tierResolver is not null)
        {
            var tier = ResolveTier(identity);
            decision = _autonomyEvaluator.Evaluate(
                tier,
                request.BlastRadius,
                request.Target.Kind,
                request.IsStateChange,
                request.SkillKey);

            if (decision.Decision == AutonomyDecision.Forbidden)
            {
                _logger.LogWarning(
                    "ChangeProposal forbidden by graded-autonomy policy: tier={Tier} radius={Radius} " +
                    "target={TargetKind} stateChange={StateChange} skill={Skill} reason={Reason}",
                    decision.Tier, decision.BlastRadius, decision.TargetKind,
                    decision.IsStateChange, decision.SkillKey ?? "<none>", decision.Reason);

                return Result<ChangeProposal>.Forbidden(
                    $"Graded autonomy policy forbids this proposal: {decision.Reason}");
            }
        }

        var gates = request.RequiredGates
            ?? _gateResolver.ResolveWithDecision(request.Target.Kind, request.BlastRadius, decision);

        if (gates.Count == 0)
        {
            return Result<ChangeProposal>.Fail(
                "IChangeProposalGateResolver returned an empty gate list — proposals must have at least one gate.");
        }

        var proposal = ChangeProposal.Create(
            target: request.Target,
            diff: request.Diff,
            submittedBy: identity,
            summary: request.Summary,
            blastRadius: request.BlastRadius,
            requiredGates: gates,
            submittedAt: submittedAt);

        var existing = await _store.GetAsync(proposal.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Idempotent ChangeProposal re-submission ignored (proposal {ProposalId} already exists in status {Status}).",
                existing.Id,
                existing.Status);
            return Result<ChangeProposal>.Success(existing);
        }

        await _store.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);

        // Hand off to the background worker. The orchestrator runs out-of-band;
        // this command returns the Draft snapshot, and the caller polls /
        // subscribes for the post-pipeline status.
        await _dispatchQueue.EnqueueAsync(proposal.Id, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(proposal);
    }

    /// <summary>
    /// Best-effort tier resolution for graded autonomy. Tries to parse the agent
    /// identity's id as a <see cref="SubagentType"/>; falls back to the configured
    /// <c>PermissionsConfig.DefaultAutonomyLevel</c>; falls back finally to
    /// <see cref="AutonomyLevel.Supervised"/> (the safe-middle default).
    /// </summary>
    private AutonomyLevel ResolveTier(Domain.AI.Identity.AgentIdentity identity)
    {
        if (_tierResolver is null)
        {
            return AutonomyLevel.Supervised;
        }

        if (Enum.TryParse<SubagentType>(identity.Id, ignoreCase: true, out var subagentType))
        {
            return _tierResolver.Resolve(subagentType);
        }

        var permissions = _config.CurrentValue.AI.Permissions;
        if (Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out var configured))
        {
            return configured;
        }

        _logger.LogWarning(
            "Could not parse '{Id}' as SubagentType and PermissionsConfig.DefaultAutonomyLevel " +
            "'{Default}' is invalid — falling back to Supervised for graded-autonomy evaluation.",
            identity.Id, permissions.DefaultAutonomyLevel);
        return AutonomyLevel.Supervised;
    }
}
