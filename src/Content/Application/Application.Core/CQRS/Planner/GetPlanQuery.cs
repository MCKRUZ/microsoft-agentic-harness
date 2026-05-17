using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Retrieves a plan graph along with its current per-step execution state.
/// </summary>
public sealed record GetPlanQuery : IRequest<Result<PlanSnapshot>>
{
    /// <summary>Identifier of the plan to retrieve.</summary>
    public required PlanId PlanId { get; init; }
}
