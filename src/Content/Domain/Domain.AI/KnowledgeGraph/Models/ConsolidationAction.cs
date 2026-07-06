namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The outcome of a consolidation decision: whether a candidate memory should be merged into an
/// existing entry or persisted as a new one. See <see cref="MemoryConsolidationDecision"/>.
/// </summary>
public enum ConsolidationAction
{
    /// <summary>Persist the candidate as a new, distinct memory entry.</summary>
    Create = 0,

    /// <summary>
    /// Merge the candidate into an existing entry (identified by
    /// <see cref="MemoryConsolidationDecision.TargetId"/>), appending to that entry's history rather than
    /// fragmenting the concept across records.
    /// </summary>
    Merge = 1,
}
