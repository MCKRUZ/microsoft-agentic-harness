namespace Domain.AI.KnowledgeGraph;

/// <summary>
/// Types of auditable memory operations tracked by <see cref="Models.MemoryAuditEvent"/>.
/// </summary>
public enum MemoryAuditAction
{
    /// <summary>A fact was stored in the knowledge graph.</summary>
    Remember,
    /// <summary>Knowledge was retrieved from the graph.</summary>
    Recall,
    /// <summary>A specific fact was deleted from the graph.</summary>
    Forget,
    /// <summary>Feedback was applied to improve knowledge quality.</summary>
    Improve,
    /// <summary>A right-to-erasure request was executed, cascading across all stores.</summary>
    Erasure
}
