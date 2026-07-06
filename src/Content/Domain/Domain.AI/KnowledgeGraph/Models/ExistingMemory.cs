namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A storage-neutral view of an already-persisted memory entry, passed to an
/// <c>IMemoryConsolidator</c> as a merge candidate. Carries only what the consolidation decision
/// needs — an id, the entry's primary abstraction, and its full value — so the seam stays decoupled
/// from any particular store representation (graph node, record, or otherwise).
/// </summary>
public sealed record ExistingMemory
{
    /// <summary>The id of the existing memory entry, echoed back in a merge decision's target.</summary>
    public required string Id { get; init; }

    /// <summary>The existing entry's primary abstraction — the canonical unit compared for consolidation.</summary>
    public required string Abstraction { get; init; }

    /// <summary>The existing entry's full memory value, for the consolidator to compare against.</summary>
    public required string Value { get; init; }
}
