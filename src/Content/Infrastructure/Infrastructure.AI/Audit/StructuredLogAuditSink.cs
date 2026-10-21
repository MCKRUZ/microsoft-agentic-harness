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
    /// <remarks>
    /// Writes the correlation id and the metadata as well: a record whose detail lives in its metadata (the erasure
    /// record's subject reference and per-store counts, a registry refresh's counts, the phase that pairs an attempt with
    /// its outcome) is otherwise reduced to a line that proves nothing. Metadata is redacted by its producer, per
    /// <c>IAuditable.AuditMetadata</c>.
    /// </remarks>
    public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Audit: {Action} on {RequestType} by {ExecutorId} — {Outcome}{FailureReason} [correlation {CorrelationId}] {AuditMetadata}",
            entry.Action,
            entry.RequestType,
            entry.ExecutorId ?? "system",
            entry.Outcome,
            entry.FailureReason is not null ? $" ({entry.FailureReason})" : string.Empty,
            entry.CorrelationId ?? "none",
            Render(entry.Metadata));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The metadata as <c>key=value</c> pairs in key order, so the same record always renders the same way. The
    /// delimiters and line breaks are percent-escaped, so a value can never forge another field or a second log line.
    /// </summary>
    private static string Render(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null || metadata.Count == 0
            ? string.Empty
            : string.Join("; ", metadata.OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => $"{Escape(m.Key)}={Escape(m.Value)}"));

    private static string Escape(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
}
