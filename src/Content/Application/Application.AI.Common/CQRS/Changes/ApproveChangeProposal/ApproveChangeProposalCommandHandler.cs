using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>
/// Handles <see cref="ApproveChangeProposalCommand"/>: load the proposal, transition
/// to <see cref="ChangeProposalStatus.Approved"/>, append the gate-history entry,
/// persist.
/// </summary>
public sealed class ApproveChangeProposalCommandHandler
    : IRequestHandler<ApproveChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed-DI key recorded on the approval gate decision in the audit history.</summary>
    public const string ApprovalGateKey = "approval";

    private readonly IChangeProposalStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
    public ApproveChangeProposalCommandHandler(IChangeProposalStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        ApproveChangeProposalCommand request,
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
                $"Cannot approve proposal in status {proposal.Status} (must be AwaitingApproval).");
        }

        var decision = new GateDecision
        {
            Timestamp = _time.GetUtcNow(),
            GateKey = ApprovalGateKey,
            Action = GateAction.Pass,
            Reason = string.IsNullOrEmpty(request.Reason) ? "approved" : request.Reason,
            ReviewerId = request.ReviewerId,
            DurationMs = 0
        };

        var approved = proposal.TransitionTo(ChangeProposalStatus.Approved, decision);
        await _store.SaveAsync(approved, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(approved);
    }
}
