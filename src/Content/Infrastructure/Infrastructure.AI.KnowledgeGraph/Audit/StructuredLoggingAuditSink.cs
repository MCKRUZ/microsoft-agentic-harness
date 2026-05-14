using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Audit;

/// <summary>
/// Audit sink that writes <see cref="MemoryAuditEvent"/> records as structured log entries.
/// Works with any log aggregator (Seq, Application Insights, ELK) via ILogger.
/// </summary>
public sealed class StructuredLoggingAuditSink : IMemoryAuditSink
{
    private readonly ILogger<StructuredLoggingAuditSink> _logger;

    public StructuredLoggingAuditSink(ILogger<StructuredLoggingAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task EmitAsync(MemoryAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        LogEvent(auditEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EmitBatchAsync(IReadOnlyList<MemoryAuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        foreach (var auditEvent in auditEvents)
            LogEvent(auditEvent);
        return Task.CompletedTask;
    }

    private void LogEvent(MemoryAuditEvent e)
    {
        _logger.LogInformation(
            "MemoryAudit: Action={Action} Actor={ActorId} Scope={ScopeId} Nodes={NodeCount} Edges={EdgeCount} EventId={EventId}",
            e.Action, e.ActorId, e.ScopeId,
            e.AffectedNodeIds?.Count ?? 0,
            e.AffectedEdgeIds?.Count ?? 0,
            e.EventId);
    }
}
