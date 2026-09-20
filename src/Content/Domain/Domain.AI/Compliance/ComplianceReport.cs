using Domain.AI.Changes;
using Domain.AI.DriftDetection;
using Domain.AI.Egress;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Observability.Models;

namespace Domain.AI.Compliance;

/// <summary>
/// A point-in-time compliance report joining every tamper-evident audit trail (governance,
/// change, egress, escalation, drift) and the conversation database (sessions, safety-filter
/// events, generic audit-log entries) into one document, scoped to a time window and optionally
/// one conversation (#696).
/// </summary>
/// <remarks>
/// Every raw record list is capped by the query that produced the report (see
/// <c>GenerateComplianceReportQuery.MaxRecordsPerSource</c>) — a report is not a full export of
/// unbounded audit history, it is bounded evidence for the requested window. <see cref="Warnings"/>
/// is the honesty mechanism: any source that failed to answer lands there, never silently as an
/// empty section that reads as "nothing happened."
/// </remarks>
public sealed record ComplianceReport
{
    /// <summary>When this report was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The identity of the caller who requested this report.</summary>
    public required string GeneratedBy { get; init; }

    /// <summary>Start of the reporting window (inclusive).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>End of the reporting window (inclusive).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>
    /// The single conversation this report is scoped to, or <see langword="null"/> when the
    /// report covers every conversation in the period.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>Aggregate session/cost/token statistics for the period.</summary>
    public required ComplianceSessionSummary Sessions { get; init; }

    /// <summary>Aggregate safety-filter statistics for the period.</summary>
    public required ComplianceSafetySummary Safety { get; init; }

    /// <summary>Safety-filter events matching the period (and conversation, if scoped), capped.</summary>
    public required IReadOnlyList<SafetyEventRecord> SafetyEvents { get; init; }

    /// <summary>Generic audit-log entries matching the period, capped.</summary>
    public required IReadOnlyList<AuditEntry> AuditEntries { get; init; }

    /// <summary>Governance tool-call decisions matching the period, capped.</summary>
    public required IReadOnlyList<GovernanceAuditRecord> GovernanceDecisions { get; init; }

    /// <summary>Change-proposal gate decisions matching the period, capped.</summary>
    public required IReadOnlyList<ChangeAuditRecord> ChangeDecisions { get; init; }

    /// <summary>Egress (outbound network) policy decisions matching the period, capped.</summary>
    public required IReadOnlyList<EgressAuditRecord> EgressDecisions { get; init; }

    /// <summary>Escalation lifecycle events matching the period, capped.</summary>
    public required IReadOnlyList<EscalationAuditRecord> EscalationEvents { get; init; }

    /// <summary>Drift-detection lifecycle events matching the period, capped.</summary>
    public required IReadOnlyList<DriftAuditRecord> DriftFindings { get; init; }

    /// <summary>
    /// Tamper-evidence verification status for every hash-chained audit trail, captured at
    /// generation time — the "we checked our own log for tampering" line of the report.
    /// </summary>
    public required IReadOnlyList<ComplianceChainIntegrity> ChainIntegrity { get; init; }

    /// <summary>
    /// One entry per data source that failed to answer rather than returning genuinely empty
    /// results. An empty report with a non-empty <see cref="Warnings"/> list is not a clean
    /// report — it is an incomplete one.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Aggregate session, cost, and token statistics for a compliance report's period.</summary>
public sealed record ComplianceSessionSummary
{
    /// <summary>Total sessions started in the period.</summary>
    public required int TotalSessions { get; init; }

    /// <summary>Sessions that ended with <see cref="SessionStatus.Completed"/>.</summary>
    public required int CompletedSessions { get; init; }

    /// <summary>Sessions that ended with <see cref="SessionStatus.Error"/>.</summary>
    public required int ErroredSessions { get; init; }

    /// <summary>Sessions that ended with <see cref="SessionStatus.Cancelled"/>.</summary>
    public required int CancelledSessions { get; init; }

    /// <summary>Total cost across every session in the period.</summary>
    public required decimal TotalCostUsd { get; init; }

    /// <summary>Total input tokens across every session in the period.</summary>
    public required long TotalInputTokens { get; init; }

    /// <summary>Total output tokens across every session in the period.</summary>
    public required long TotalOutputTokens { get; init; }
}

/// <summary>Aggregate safety-filter statistics for a compliance report's period.</summary>
public sealed record ComplianceSafetySummary
{
    /// <summary>Total safety events evaluated in the period.</summary>
    public required int TotalEvents { get; init; }

    /// <summary>Events with outcome <c>"block"</c>.</summary>
    public required int BlockedCount { get; init; }

    /// <summary>Events with outcome <c>"redact"</c>.</summary>
    public required int RedactedCount { get; init; }

    /// <summary>Blocked/redacted event counts grouped by category (e.g. <c>"pii"</c>, <c>"hate"</c>).</summary>
    public required IReadOnlyDictionary<string, int> CountsByCategory { get; init; }
}

/// <summary>One audit chain's tamper-evidence verification result, captured for a compliance report.</summary>
public sealed record ComplianceChainIntegrity
{
    /// <summary>The chain's stable name (e.g. <c>"governance"</c>, <c>"drift"</c>).</summary>
    public required string ChainName { get; init; }

    /// <summary>Whether the chain verified intact end-to-end.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Records verified cleanly from genesis before any break (or the full count, if intact).</summary>
    public required long VerifiedCount { get; init; }

    /// <summary>Explanation of the break, when <see cref="IsValid"/> is <see langword="false"/>.</summary>
    public string? FailureReason { get; init; }
}
