using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.ListChangeProposals;

/// <summary>
/// List <see cref="ChangeProposal"/>s matching a <see cref="ChangeProposalQuery"/> filter.
/// </summary>
public sealed record ListChangeProposalsQuery : IRequest<Result<IReadOnlyList<ChangeProposal>>>
{
    /// <summary>The filter criteria. Null filter dimensions match everything; <see cref="ChangeProposalQuery.MaxResults"/> caps the result count.</summary>
    public required ChangeProposalQuery Filter { get; init; }
}
