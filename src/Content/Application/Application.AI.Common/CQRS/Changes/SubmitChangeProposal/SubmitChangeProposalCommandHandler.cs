using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Changes.SubmitChangeProposal;

/// <summary>
/// Handles <see cref="SubmitChangeProposalCommand"/>: resolves required gates, derives
/// the deterministic id, and persists the proposal. Idempotent — a duplicate submission
/// within the same id-bucket returns the prior proposal verbatim instead of creating
/// a parallel pipeline.
/// </summary>
public sealed class SubmitChangeProposalCommandHandler
    : IRequestHandler<SubmitChangeProposalCommand, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalGateResolver _gateResolver;
    private readonly IAgentExecutionContext _agentContext;
    private readonly TimeProvider _time;
    private readonly ILogger<SubmitChangeProposalCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="SubmitChangeProposalCommandHandler"/>.</summary>
    public SubmitChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalGateResolver gateResolver,
        IAgentExecutionContext agentContext,
        TimeProvider time,
        ILogger<SubmitChangeProposalCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateResolver);
        ArgumentNullException.ThrowIfNull(agentContext);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _gateResolver = gateResolver;
        _agentContext = agentContext;
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

        var identity = _agentContext.AgentIdentity;
        if (identity is null)
        {
            return Result<ChangeProposal>.Unauthorized(
                "SubmitChangeProposalCommand requires an ambient agent identity. " +
                "Caller must execute inside an agent scope established by AgentIdentityResolutionBehavior.");
        }

        var submittedAt = request.SubmittedAt ?? _time.GetUtcNow();
        var gates = request.RequiredGates ?? _gateResolver.Resolve(request.Target.Kind, request.BlastRadius);

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
        return Result<ChangeProposal>.Success(proposal);
    }
}
