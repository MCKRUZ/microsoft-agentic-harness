using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.ListChangeProposals;

/// <summary>Handles <see cref="ListChangeProposalsQuery"/> by delegating to the store.</summary>
public sealed class ListChangeProposalsQueryHandler
    : IRequestHandler<ListChangeProposalsQuery, Result<IReadOnlyList<ChangeProposal>>>
{
    private readonly IChangeProposalStore _store;

    /// <summary>Initializes a new <see cref="ListChangeProposalsQueryHandler"/>.</summary>
    public ListChangeProposalsQueryHandler(IChangeProposalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ChangeProposal>>> Handle(
        ListChangeProposalsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var results = await _store.ListAsync(request.Filter, cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<ChangeProposal>>.Success(results);
    }
}
