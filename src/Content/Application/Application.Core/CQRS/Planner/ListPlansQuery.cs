using Domain.AI.Planner;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Planner;

/// <summary>
/// Lists plans with optional filtering by status and date range.
/// </summary>
public sealed record ListPlansQuery : IRequest<Result<IReadOnlyList<PlanGraph>>>
{
    /// <summary>Optional filter by step execution status.</summary>
    public StepExecutionStatus? StatusFilter { get; init; }

    /// <summary>Optional start of time range filter.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Optional end of time range filter.</summary>
    public DateTimeOffset? To { get; init; }
}
