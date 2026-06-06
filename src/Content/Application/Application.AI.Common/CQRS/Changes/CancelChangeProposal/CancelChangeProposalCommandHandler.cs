using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.CancelChangeProposal;

/// <summary>
/// Handles <see cref="CancelChangeProposalCommand"/>: transitions a non-terminal,
/// non-merging proposal to <see cref="ChangeProposalStatus.Cancelled"/>. Cancel
/// is illegal once <c>Merging</c> has started; that's enforced by the state machine.
/// </summary>
public sealed class CancelChangeProposalCommandHandler
    : IRequestHandler<CancelChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed gate decision identifier used for cancellation history entries.</summary>
    public const string CancellationGateKey = "cancellation";

    private readonly IChangeProposalStore _store;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="CancelChangeProposalCommandHandler"/>.</summary>
    public CancelChangeProposalCommandHandler(IChangeProposalStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        CancelChangeProposalCommand request,
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

        if (proposal.IsTerminal)
        {
            return Result<ChangeProposal>.Fail(
                $"Cannot cancel proposal in terminal status {proposal.Status}.");
        }

        if (proposal.Status == ChangeProposalStatus.Merging)
        {
            return Result<ChangeProposal>.Fail(
                "Cannot cancel proposal while merge is in progress.");
        }

        var decision = new GateDecision
        {
            Timestamp = _time.GetUtcNow(),
            GateKey = CancellationGateKey,
            Action = GateAction.Fail,
            Reason = string.IsNullOrEmpty(request.Reason)
                ? $"cancelled by {request.CancelledBy}"
                : request.Reason,
            ReviewerId = request.CancelledBy,
            DurationMs = 0
        };

        var cancelled = proposal.TransitionTo(ChangeProposalStatus.Cancelled, decision);
        await _store.SaveAsync(cancelled, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(cancelled);
    }
}
