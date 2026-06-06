using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.CancelChangeProposal;

/// <summary>
/// Cancellation initiated by the submitting agent or by a human, before the
/// proposal reaches <see cref="ChangeProposalStatus.Merging"/>. Distinct from
/// rejection — no gate produced an adverse decision, the submitter simply
/// withdrew the change.
/// </summary>
public sealed record CancelChangeProposalCommand : IRequest<Result<ChangeProposal>>
{
    /// <summary>The id of the proposal to cancel.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Id of the entity (agent or user) cancelling. Captured in the gate-history audit entry.</summary>
    public required string CancelledBy { get; init; }

    /// <summary>Optional short reason. Surfaces in audit.</summary>
    public string Reason { get; init; } = string.Empty;
}
