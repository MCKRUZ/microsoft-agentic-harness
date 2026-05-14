using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.KnowledgeGraph.Audit;

/// <summary>
/// No-op audit sink that discards all events. Used when compliance auditing is disabled.
/// </summary>
public sealed class NoOpAuditSink : IMemoryAuditSink
{
    /// <inheritdoc />
    public Task EmitAsync(MemoryAuditEvent auditEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task EmitBatchAsync(IReadOnlyList<MemoryAuditEvent> auditEvents, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
