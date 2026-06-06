using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.RejectChangeProposal;

/// <summary>
/// Handles <see cref="RejectChangeProposalCommand"/>: load, transition to
/// <see cref="ChangeProposalStatus.Rejected"/>, append the gate-history entry,
/// persist. Recorded under the same <c>approval</c> gate key as approvals so
/// dashboards can group both decisions by gate.
/// </summary>
public sealed class RejectChangeProposalCommandHandler
    : IRequestHandler<RejectChangeProposalCommand, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="RejectChangeProposalCommandHandler"/>.</summary>
    public RejectChangeProposalCommandHandler(IChangeProposalStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        RejectChangeProposalCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var proposal = await _store.GetAsync(request.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            return Result<ChangeProposal>.NotFound(
                $"ChangeProposal '{request.ProposalId}' not found.");
        }

        if (proposal.Status != ChangeProposalStatus.AwaitingApproval)
        {
            return Result<ChangeProposal>.Fail(
                $"Cannot reject proposal in status {proposal.Status} (must be AwaitingApproval).");
        }

        var decision = new GateDecision
        {
            Timestamp = _time.GetUtcNow(),
            GateKey = ApproveChangeProposalCommandHandler.ApprovalGateKey,
            Action = GateAction.Fail,
            Reason = request.Reason,
            ReviewerId = request.ReviewerId,
            DurationMs = 0
        };

        var rejected = proposal.TransitionTo(ChangeProposalStatus.Rejected, decision);
        await _store.SaveAsync(rejected, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(rejected);
    }
}
