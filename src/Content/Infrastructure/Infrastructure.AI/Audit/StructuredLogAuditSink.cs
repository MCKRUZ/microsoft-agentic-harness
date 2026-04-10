using Application.AI.Common.Interfaces.Agent;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Audit sink that writes entries to structured logs via <see cref="ILogger"/>.
/// Suitable for local development, POC, and any environment with a log aggregator
/// (Seq, Application Insights, ELK) that can query structured fields.
/// </summary>
/// <remarks>
/// For production compliance, replace or supplement with a durable sink
/// (Azure Table Storage, SQL, event stream) that guarantees write persistence.
/// </remarks>
public sealed class StructuredLogAuditSink : IAuditSink
{
    private readonly ILogger<StructuredLogAuditSink> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogAuditSink"/> class.
    /// </summary>
    public StructuredLogAuditSink(ILogger<StructuredLogAuditSink> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Audit: {Action} on {RequestType} by {ExecutorId} — {Outcome}{FailureReason}",
            entry.Action,
            entry.RequestType,
            entry.ExecutorId ?? "system",
            entry.Outcome,
            entry.FailureReason is not null ? $" ({entry.FailureReason})" : string.Empty);

        return ValueTask.CompletedTask;
    }
}
