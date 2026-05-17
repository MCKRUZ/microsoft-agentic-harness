using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Persists a pre-built plan graph after validation.
/// </summary>
public sealed record CreatePlanCommand : IRequest<Result<PlanId>>
{
    /// <summary>The plan graph to validate and persist.</summary>
    public required PlanGraph Plan { get; init; }
}
