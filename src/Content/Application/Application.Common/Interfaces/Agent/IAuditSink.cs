using Application.Common.Models;

namespace Application.Common.Interfaces.Agent;

/// <summary>
/// Abstraction over the audit storage mechanism. Consumed by <c>AuditTrailBehavior</c>
/// to persist audit entries for compliance and traceability.
/// </summary>
/// <remarks>
/// Implementation can start as a structured log writer and evolve to a database,
/// Azure Table Storage, or event stream as compliance requirements mature.
/// </remarks>
public interface IAuditSink
{
    /// <summary>
    /// Records an audit entry.
    /// </summary>
    /// <param name="entry">The audit entry to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}
