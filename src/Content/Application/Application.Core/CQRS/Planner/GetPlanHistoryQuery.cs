using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Retrieves the execution audit trail for a plan.
/// </summary>
public sealed record GetPlanHistoryQuery : IRequest<Result<IReadOnlyList<PlanExecutionLogEntry>>>
{
    /// <summary>Identifier of the plan whose history to retrieve.</summary>
    public required PlanId PlanId { get; init; }
}
