using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Cancels a running plan execution. Steps already in progress complete but no new steps start.
/// </summary>
public sealed record CancelPlanCommand : IRequest<Result>
{
    /// <summary>Identifier of the plan to cancel.</summary>
    public required PlanId PlanId { get; init; }
}
