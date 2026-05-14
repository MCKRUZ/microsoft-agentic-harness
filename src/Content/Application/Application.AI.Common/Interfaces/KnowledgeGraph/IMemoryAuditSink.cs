using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Pluggable sink for memory audit events. Implementations determine where audit
/// records are stored (structured logs, database, event hub, etc.).
/// </summary>
public interface IMemoryAuditSink
{
    /// <summary>Emit a single audit event.</summary>
    Task EmitAsync(MemoryAuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>Emit multiple audit events in a batch.</summary>
    Task EmitBatchAsync(IReadOnlyList<MemoryAuditEvent> auditEvents, CancellationToken cancellationToken = default);
}
