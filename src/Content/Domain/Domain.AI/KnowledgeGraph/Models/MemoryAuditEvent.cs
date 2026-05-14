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
    /// <summary>Node IDs affected by the operation. Null for query-only operations.</summary>
    public IReadOnlyList<string>? AffectedNodeIds { get; init; }
    /// <summary>Edge IDs affected by the operation. Null for query-only operations.</summary>
    public IReadOnlyList<string>? AffectedEdgeIds { get; init; }
    /// <summary>The search query (for Recall events).</summary>
    public string? Query { get; init; }
    /// <summary>Number of results returned (for Recall events).</summary>
    public int? ResultCount { get; init; }
}
