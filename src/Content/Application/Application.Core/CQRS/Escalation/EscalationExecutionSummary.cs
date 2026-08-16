using Domain.AI.Escalation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// A wire-safe projection of a resolved escalation's execution outcome (#396): what happened
/// when the approved action was actually carried out, once <c>IApprovalExecutionReporter</c> has
/// reported it. Absent from <see cref="EscalationOutcomeSummary"/> until then — a denied
/// escalation, or an approved one whose action hasn't run yet, has no execution outcome to show.
/// </summary>
public sealed record EscalationExecutionSummary
{
    /// <summary>Whether the action succeeded, failed, or never ran.</summary>
    public required EscalationExecutionStatus Status { get; init; }

    /// <summary>Why the action failed. Non-null if and only if <see cref="Status"/> is <see cref="EscalationExecutionStatus.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Why the action never ran. Non-null if and only if <see cref="Status"/> is <see cref="EscalationExecutionStatus.NeverExecuted"/>.</summary>
    public EscalationNotExecutedReason? NotExecutedReason { get; init; }

    /// <summary>When this outcome was reported.</summary>
    public required DateTimeOffset ReportedAt { get; init; }

    /// <summary>The site that reported this outcome (e.g. <c>"direct-invocation"</c>, <c>"agent-turn"</c>, <c>"plan-executor"</c>).</summary>
    public required string ReportedBy { get; init; }

    /// <summary>Projects a domain <see cref="EscalationExecutionRecord"/> to the wire-safe shape.</summary>
    /// <param name="record">The recorded execution outcome to project. Must not be null.</param>
    public static EscalationExecutionSummary FromRecord(EscalationExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new EscalationExecutionSummary
        {
            Status = record.Status,
            FailureReason = record.FailureReason,
            NotExecutedReason = record.NotExecutedReason,
            ReportedAt = record.ReportedAt,
            ReportedBy = record.ReportedBy
        };
    }
}
