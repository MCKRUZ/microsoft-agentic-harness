using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.GetChangeProposal;

/// <summary>Handles <see cref="GetChangeProposalQuery"/> by delegating to the store.</summary>
public sealed class GetChangeProposalQueryHandler
    : IRequestHandler<GetChangeProposalQuery, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;

    /// <summary>Initializes a new <see cref="GetChangeProposalQueryHandler"/>.</summary>
    public GetChangeProposalQueryHandler(IChangeProposalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        GetChangeProposalQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var proposal = await _store.GetAsync(request.Id, cancellationToken).ConfigureAwait(false);
        return proposal is null
            ? Result<ChangeProposal>.NotFound($"ChangeProposal '{request.Id}' not found.")
            : Result<ChangeProposal>.Success(proposal);
    }
}
