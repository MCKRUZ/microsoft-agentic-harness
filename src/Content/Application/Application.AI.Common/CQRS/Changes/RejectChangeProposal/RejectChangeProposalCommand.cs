using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.RejectChangeProposal;

/// <summary>
/// External (human) rejection of a proposal currently in
/// <see cref="ChangeProposalStatus.AwaitingApproval"/>. Drives the proposal to
/// terminal <see cref="ChangeProposalStatus.Rejected"/>.
/// </summary>
public sealed record RejectChangeProposalCommand : IRequest<Result<ChangeProposal>>
{
    /// <summary>The id of the proposal to reject.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Id of the human rejecting. Captured in the gate-history audit entry.</summary>
    public required string ReviewerId { get; init; }

    /// <summary>Required reason for the rejection. Surfaces in the audit trail and back to the submitting agent.</summary>
    public required string Reason { get; init; }
}
