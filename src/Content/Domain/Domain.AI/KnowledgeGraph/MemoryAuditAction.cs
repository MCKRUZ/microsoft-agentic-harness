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
    Erasure,
    /// <summary>
    /// Nodes past their retention period were removed by the retention sweep (#901). Kept apart from
    /// <see cref="Erasure"/> so the audit trail still shows how erasure requests were handled.
    /// </summary>
    Expiry,
    /// <summary>
    /// A source document was removed from the corpus, with the knowledge extracted from it (#901). Kept apart from
    /// <see cref="Erasure"/>: it is about a document, not a person.
    /// </summary>
    DocumentRemoval
}
