using Domain.AI.Learnings;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Filter criteria for searching learning entries. Used by <see cref="ILearningsStore"/>.
/// All optional fields act as AND filters when provided.
/// </summary>
public sealed record LearningSearchCriteria
{
    /// <summary>Scope for filtering. Null searches across all scopes. Agent-scoped queries also return team and global learnings.</summary>
    public LearningScope? Scope { get; init; }

    /// <summary>Filter by learning category. Null returns all categories.</summary>
    public LearningCategory? Category { get; init; }

    /// <summary>Minimum feedback weight threshold. Null disables weight filtering.</summary>
    public double? MinFeedbackWeight { get; init; }

    /// <summary>Only return learnings created after this date. Null disables.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Only return learnings created before this date. Null disables.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }
}
