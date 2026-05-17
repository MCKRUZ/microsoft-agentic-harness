using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Retrieves the step execution audit trail for a plan from the state store.
/// </summary>
public sealed class GetPlanHistoryQueryHandler : IRequestHandler<GetPlanHistoryQuery, Result<IReadOnlyList<PlanExecutionLogEntry>>>
{
    private readonly IPlanStateStore _store;

    public GetPlanHistoryQueryHandler(IPlanStateStore store)
    {
        _store = store;
    }

    public async Task<Result<IReadOnlyList<PlanExecutionLogEntry>>> Handle(
        GetPlanHistoryQuery request, CancellationToken cancellationToken)
    {
        return await _store.GetExecutionHistoryAsync(request.PlanId, cancellationToken);
    }
}
