namespace Domain.AI.KnowledgeGraph.Enums;

/// <summary>
/// The four cross-session memory operations supported by ICrossSessionMemoryStore.
/// </summary>
public enum MemoryOperation
{
    /// <summary>Store a new fact or update an existing memory.</summary>
    Remember,

    /// <summary>Retrieve memories matching a query, updating access metadata.</summary>
    Recall,

    /// <summary>Explicitly delete a memory by ID.</summary>
    Forget,

    /// <summary>Apply feedback delta to adjust a memory's weight.</summary>
    Improve
}
