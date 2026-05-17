using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Generates a plan from a natural-language task description using LLM inference,
/// validates the result, and persists it.
/// </summary>
public sealed record GeneratePlanCommand : IRequest<Result<PlanId>>
{
    /// <summary>Natural-language description of the task to plan.</summary>
    public required string TaskDescription { get; init; }

    /// <summary>Optional constraints on plan complexity and structure.</summary>
    public PlanGenerationConstraints? Constraints { get; init; }
}
