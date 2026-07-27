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

    /// <summary>
    /// Whether this recall reinforces the recalled learnings' access metadata
    /// (<c>LastAccessedAt</c>, via a fire-and-forget <see cref="RecordLearningAccessCommand"/>).
    /// Default true — the in-process agent-turn recall path keeps its behavior unchanged.
    /// </summary>
    /// <remarks>
    /// The HTTP recall surface (<see cref="RecallLearningsQuery"/>) sets this to false: an HTTP
    /// GET must not perform caller-steered store writes — a role-holder looping the endpoint
    /// would rewrite <c>LastAccessedAt</c> on whichever learnings its context matches, and the
    /// read-modify-write access update can race (and clobber) a concurrent feedback-weight
    /// improvement.
    /// </remarks>
    public bool RecordAccess { get; init; } = true;
}
