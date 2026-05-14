using Domain.AI.Learnings;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Retrieves learnings relevant to the given context, ranked by relevance and feedback weight.
/// </summary>
public sealed record RecallQuery : IRequest<Result<IReadOnlyList<WeightedLearning>>>
{
    /// <summary>Natural language context to match against stored learnings.</summary>
    public required string Context { get; init; }

    /// <summary>Scope for filtering (includes hierarchical scope resolution).</summary>
    public required LearningScope Scope { get; init; }

    /// <summary>Maximum number of results to return. Default 10.</summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>Minimum relevance score threshold (0.0-1.0). Default 0.0 (no filter).</summary>
    public double MinRelevance { get; init; } = 0.0;
}
