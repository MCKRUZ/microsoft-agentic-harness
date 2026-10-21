namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// An auditable event emitted by the memory compliance layer. Consumed by
/// <c>IMemoryAuditSink</c> implementations for compliance logging.
/// </summary>
public record MemoryAuditEvent
{
    /// <summary>Unique identifier for this audit event.</summary>
    public required string EventId { get; init; }
    /// <summary>The type of memory operation.</summary>
    public required MemoryAuditAction Action { get; init; }
    /// <summary>The user or system that performed the operation.</summary>
    public required string ActorId { get; init; }
    /// <summary>When the operation occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>The knowledge scope in which the operation occurred.</summary>
    public required string ScopeId { get; init; }
    /// <summary>
    /// Node IDs affected by the operation. Null for query-only operations, and for deletions, which carry
    /// <see cref="AffectedNodeCount"/> instead.
    /// </summary>
    /// <remarks>
    /// A record of a deletion must not keep what was deleted: memory node ids embed the owner and the memory key
    /// (<c>memory:{tenant}:{user}:{key}</c>), so an audit trail that listed them would outlive the erasure it records (#900).
    /// </remarks>
    public IReadOnlyList<string>? AffectedNodeIds { get; init; }
    /// <summary>Edge IDs affected by the operation. Null for query-only operations and for deletions, like <see cref="AffectedNodeIds"/>.</summary>
    public IReadOnlyList<string>? AffectedEdgeIds { get; init; }
    /// <summary>How many nodes a deletion removed. Null for events that list <see cref="AffectedNodeIds"/>.</summary>
    public int? AffectedNodeCount { get; init; }
    /// <summary>How many edges a deletion removed. Null for events that list <see cref="AffectedEdgeIds"/>.</summary>
    public int? AffectedEdgeCount { get; init; }
    /// <summary>The search query (for Recall events).</summary>
    public string? Query { get; init; }
    /// <summary>Number of results returned (for Recall events).</summary>
    public int? ResultCount { get; init; }
}
