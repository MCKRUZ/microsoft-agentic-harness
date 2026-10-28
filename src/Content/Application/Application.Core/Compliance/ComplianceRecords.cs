using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Agent;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Application.Core.Compliance;

/// <summary>
/// Writes one erasure, export or document-removal record to both of its destinations (#969): the compliance audit trail,
/// which keeps it as proof, and the audit sink, whose default log line serves log tooling. Shared by
/// <see cref="AuditedErasure"/>, <see cref="AuditedExport"/> and <see cref="AuditedDocumentRemoval"/> so the three keep
/// one policy.
/// </summary>
/// <remarks>
/// Each write is attempted whatever happened to the other, never cancelled by the caller (a request that started must leave
/// its outcome even when it is abandoned part-way), and never throws: a broken destination is logged, and must not refuse a
/// person their request. Exceptions are logged with their message withheld, since a store's message can name the person.
/// </remarks>
internal static class ComplianceRecords
{
    /// <summary>Writes <paramref name="entry"/> to the trail, then to the sink.</summary>
    /// <param name="trail">The compliance audit trail.</param>
    /// <param name="sink">The general audit sink.</param>
    /// <param name="entry">The record.</param>
    /// <param name="actor">Who asked; the trail decides from it how to record them.</param>
    /// <param name="logger">The calling class's logger, for write failures.</param>
    /// <param name="requestKind">The request as a log line names it, for example <c>right-to-erasure</c>.</param>
    /// <returns>A task that completes once both writes have been attempted.</returns>
    internal static async Task WriteAsync(
        IComplianceAuditTrail trail, IAuditSink sink, AuditEntry entry, ErasureActor actor, ILogger logger, string requestKind)
    {
        var phase = entry.Metadata?.GetValueOrDefault("phase");
        try
        {
            var kept = await trail.RecordAsync(entry, actor, CancellationToken.None);
            if (!kept.IsSuccess)
                logger.LogError(
                    "Failed to keep the {Phase} of {RequestKind} {CorrelationId} in the compliance audit trail: {Reason}",
                    phase, requestKind, entry.CorrelationId, string.Join("; ", kept.Errors));
        }
        catch (Exception ex)
        {
            logger.LogError(MessageWithheldException.From(ex),
                "Failed to keep the {Phase} of {RequestKind} {CorrelationId} in the compliance audit trail.",
                phase, requestKind, entry.CorrelationId);
        }

        try
        {
            await sink.RecordAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(MessageWithheldException.From(ex), "Failed to record the {Phase} of {RequestKind} {CorrelationId}.",
                phase, requestKind, entry.CorrelationId);
        }
    }
}
