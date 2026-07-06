namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The decision returned by an <c>IMemoryConsolidator</c> for a candidate memory: create a new entry,
/// or merge into an existing one. Prevents memory fragmentation by consolidating related and evolving
/// information under a single persistent entry keyed on its primary abstraction.
/// </summary>
/// <remarks>
/// Construct via the <see cref="Create"/> and <see cref="MergeInto"/> factories, which enforce the
/// invariant that a <see cref="ConsolidationAction.Merge"/> always carries a non-empty
/// <see cref="TargetId"/> and a <see cref="ConsolidationAction.Create"/> never does.
/// </remarks>
public sealed record MemoryConsolidationDecision
{
    private MemoryConsolidationDecision()
    {
    }

    /// <summary>Whether the candidate should be created as new or merged into an existing entry.</summary>
    public required ConsolidationAction Action { get; init; }

    /// <summary>
    /// The id of the existing memory entry to merge into. Non-null exactly when
    /// <see cref="Action"/> is <see cref="ConsolidationAction.Merge"/>; null for
    /// <see cref="ConsolidationAction.Create"/>.
    /// </summary>
    public string? TargetId { get; init; }

    /// <summary>Creates a decision to persist the candidate as a new memory entry.</summary>
    public static MemoryConsolidationDecision Create() =>
        new() { Action = ConsolidationAction.Create };

    /// <summary>
    /// Creates a decision to merge the candidate into the existing entry identified by
    /// <paramref name="targetId"/>.
    /// </summary>
    /// <param name="targetId">The id of the existing memory entry to merge into. Must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="targetId"/> is null or whitespace.</exception>
    public static MemoryConsolidationDecision MergeInto(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("A merge decision requires a non-empty target id.", nameof(targetId));
        }

        return new() { Action = ConsolidationAction.Merge, TargetId = targetId };
    }
}
