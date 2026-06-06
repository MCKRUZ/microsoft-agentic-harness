using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.GetChangeProposal;

/// <summary>Read a single <see cref="ChangeProposal"/> by id.</summary>
public sealed record GetChangeProposalQuery : IRequest<Result<ChangeProposal>>
{
    /// <summary>The proposal id (Base64URL SHA-256 hash from <c>ChangeProposalIdDeriver</c>).</summary>
    public required string Id { get; init; }
}
