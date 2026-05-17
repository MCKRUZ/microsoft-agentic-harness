using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Lists plans from the state store with optional filtering.
/// </summary>
public sealed class ListPlansQueryHandler : IRequestHandler<ListPlansQuery, Result<IReadOnlyList<PlanGraph>>>
{
    private readonly IPlanStateStore _store;

    public ListPlansQueryHandler(IPlanStateStore store)
    {
        _store = store;
    }

    public async Task<Result<IReadOnlyList<PlanGraph>>> Handle(
        ListPlansQuery request, CancellationToken cancellationToken)
    {
        return await _store.ListPlansAsync(
            request.StatusFilter, request.From, request.To, cancellationToken);
    }
}
