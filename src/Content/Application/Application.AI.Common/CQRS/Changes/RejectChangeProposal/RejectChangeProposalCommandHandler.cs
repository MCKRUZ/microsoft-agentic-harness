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

        return await ChangeProposalCommandHelper.ApplyDecisionAsync(
            _store,
            request.ProposalId,
            statusGuard: p => p.Status != ChangeProposalStatus.AwaitingApproval
                ? Result<ChangeProposal>.Fail(
                    $"Cannot reject proposal in status {p.Status} (must be AwaitingApproval).")
                : null,
            decisionFactory: () => new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = ApproveChangeProposalCommandHandler.ApprovalGateKey,
                Action = GateAction.Fail,
                Reason = request.Reason,
                ReviewerId = request.ReviewerId,
                DurationMs = 0
            },
            targetStatus: ChangeProposalStatus.Rejected,
            postSave: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
