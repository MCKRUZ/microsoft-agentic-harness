using System.Text.Json.Serialization;

namespace Domain.AI.Escalation;

/// <summary>
/// What happened when an approved escalation's action was actually carried out — the record
/// pushed to the approver so a failed action and a completed one no longer look identical.
/// </summary>
/// <remarks>
/// Constructible from application code only through the factories below, mirroring
/// <c>Application.AI.Common.Interfaces.Governance.ToolCallAdmission</c>: a <see cref="Failed"/>
/// record with a blank <see cref="FailureReason"/> would read to an approver as "no reason
/// given", which is indistinguishable from success, so the shape that could produce one is kept
/// unreachable rather than defended against at every reader. The constructor is marked
/// <see cref="JsonConstructorAttribute"/> so <c>System.Text.Json</c> can rehydrate an
/// already-validated record from the audit store (#396) without bypassing this class's own
/// invariant checks for anything application code constructs fresh.
/// </remarks>
public sealed record EscalationExecutionRecord
{
    [JsonConstructor]
    private EscalationExecutionRecord(
        Guid escalationId,
        EscalationExecutionStatus status,
        string? failureReason,
        EscalationNotExecutedReason? notExecutedReason,
        DateTimeOffset reportedAt,
        string reportedBy)
    {
        EscalationId = escalationId;
        Status = status;
        FailureReason = failureReason;
        NotExecutedReason = notExecutedReason;
        ReportedAt = reportedAt;
        ReportedBy = reportedBy;
    }

    /// <summary>Correlates back to the originating escalation.</summary>
    public Guid EscalationId { get; }

    /// <summary>Whether the action succeeded, failed, or never ran.</summary>
    public EscalationExecutionStatus Status { get; }

    /// <summary>
    /// Why the action failed. Non-null if and only if <see cref="Status"/> is
    /// <see cref="EscalationExecutionStatus.Failed"/>.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Why the action never ran. Non-null if and only if <see cref="Status"/> is
    /// <see cref="EscalationExecutionStatus.NeverExecuted"/>.
    /// </summary>
    public EscalationNotExecutedReason? NotExecutedReason { get; }

    /// <summary>When this record was produced.</summary>
    public DateTimeOffset ReportedAt { get; }

    /// <summary>
    /// A stable identifier for the site that produced this record (<c>"direct-invocation"</c>,
    /// <c>"agent-turn"</c>, or <c>"plan-executor"</c>). Never blank — this is what makes "nobody
    /// reported" distinguishable from "this site reported" when auditing which raising sites
    /// implement execution reporting.
    /// </summary>
    public string ReportedBy { get; }

    /// <summary>The action ran and completed successfully.</summary>
    public static EscalationExecutionRecord Succeeded(
        Guid escalationId, DateTimeOffset reportedAt, string reportedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedBy);
        return new EscalationExecutionRecord(
            escalationId, EscalationExecutionStatus.Succeeded, null, null, reportedAt, reportedBy);
    }

    /// <summary>The action ran and failed.</summary>
    /// <param name="failureReason">
    /// Why it failed. Must contain something an approver can read — blank is rejected as well as
    /// null, for the same reason as <see cref="FailureReason"/>'s own doc.
    /// </param>
    public static EscalationExecutionRecord Failed(
        Guid escalationId, string failureReason, DateTimeOffset reportedAt, string reportedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedBy);
        return new EscalationExecutionRecord(
            escalationId, EscalationExecutionStatus.Failed, failureReason, null, reportedAt, reportedBy);
    }

    /// <summary>The action never ran.</summary>
    public static EscalationExecutionRecord NeverExecuted(
        Guid escalationId,
        EscalationNotExecutedReason reason,
        DateTimeOffset reportedAt,
        string reportedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedBy);
        return new EscalationExecutionRecord(
            escalationId, EscalationExecutionStatus.NeverExecuted, null, reason, reportedAt, reportedBy);
    }
}
