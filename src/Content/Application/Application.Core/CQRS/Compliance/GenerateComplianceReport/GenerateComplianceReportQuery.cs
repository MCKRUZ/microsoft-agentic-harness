using Domain.AI.Compliance;
using Domain.Common;
using Domain.Common.Constants;
using MediatR;

namespace Application.Core.CQRS.Compliance.GenerateComplianceReport;

/// <summary>
/// Generates a compliance report for a time window, joining every hash-chained audit trail
/// (governance, change, egress, escalation, drift) with the conversation database (sessions,
/// safety-filter events, generic audit-log entries) (#696).
/// </summary>
/// <remarks>
/// <see cref="ConversationId"/> only narrows the session/safety/audit-log sections — the five
/// audit-chain trails have no notion of a conversation (they key on agent, proposal, target,
/// escalation, or drift-event id instead), so those sections always cover the whole period
/// regardless of scope. <see cref="MaxRecordsPerSource"/> caps each source's SQL/file read
/// independently; when a report is conversation-scoped, filtering happens after that cap, so a
/// very high-traffic period can exclude older matching records for a specific conversation —
/// narrow the window or raise the cap if that matters for a given report.
/// </remarks>
public sealed record GenerateComplianceReportQuery : IRequest<Result<ComplianceReport>>
{
    /// <summary>
    /// The requesting caller's identity, recorded as <see cref="ComplianceReport.GeneratedBy"/> and
    /// in the harness's own audit log.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's stable identity
    /// claim</b> (<c>ClaimsPrincipalExtensions.GetUserIdOrNull</c>). It must never be bound from a
    /// request body, query string, or header.
    /// </remarks>
    public required string CallerId { get; init; }

    /// <summary>Start of the reporting window (inclusive).</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>
    /// End of the reporting window (inclusive). Must not precede <see cref="Start"/>, and the span
    /// between the two must not exceed <see cref="ComplianceReportDefaults.MaxWindowDays"/>.
    /// </summary>
    public required DateTimeOffset End { get; init; }

    /// <summary>
    /// Narrows the session/safety/audit-log sections to one conversation. Null covers every
    /// conversation in the window. See this type's remarks for what scoping does and does not
    /// affect.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Maximum raw records returned per data source, between 1 and
    /// <see cref="AuditQueryDefaults.MaxResults"/>. When a source has more matches, the most recent
    /// ones are kept.
    /// </summary>
    public int MaxRecordsPerSource { get; init; } = ComplianceReportDefaults.DefaultMaxRecordsPerSource;
}
