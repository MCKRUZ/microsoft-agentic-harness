using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>
/// External (human) approval for a proposal currently in
/// <see cref="ChangeProposalStatus.AwaitingApproval"/>. Triggered from the AgentHub
/// approval UI, an admin portal, or an auto-approve workflow under autonomous tier.
/// </summary>
/// <remarks>
/// Transitions <see cref="ChangeProposalStatus.AwaitingApproval"/> →
/// <see cref="ChangeProposalStatus.Approved"/>. The orchestrator picks up Approved
/// proposals and dispatches the <c>MergeGate</c>.
/// </remarks>
public sealed record ApproveChangeProposalCommand : IRequest<Result<ChangeProposal>>
{
    /// <summary>The id of the proposal to approve.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Id of the human (or auto-approver) recording the approval. Captured in the gate-history audit entry.</summary>
    public required string ReviewerId { get; init; }

    /// <summary>Optional reason / comment. Surfaces in the audit trail.</summary>
    public string Reason { get; init; } = string.Empty;
}
