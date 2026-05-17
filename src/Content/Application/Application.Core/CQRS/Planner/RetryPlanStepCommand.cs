using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Retries a specific failed step within a plan.
/// </summary>
public sealed record RetryPlanStepCommand : IRequest<Result>
{
    /// <summary>Identifier of the plan containing the step.</summary>
    public required PlanId PlanId { get; init; }

    /// <summary>Identifier of the failed step to retry.</summary>
    public required PlanStepId StepId { get; init; }
}
