using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Starts or resumes execution of a plan.
/// </summary>
public sealed record ExecutePlanCommand : IRequest<Result<PlanExecutionSummary>>
{
    /// <summary>Identifier of the plan to execute.</summary>
    public required PlanId PlanId { get; init; }
}
