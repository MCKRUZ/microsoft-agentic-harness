using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Closes the approval loop: reports what happened when an action a human approved was actually
/// carried out, and updates the bounded failure memory <see cref="IApprovalFailureMemory"/> keeps
/// so a corrected retry is attributed rather than presented as a fresh ask.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Implementations MUST NOT throw, ever.</strong> This runs strictly after the action it
/// describes has already completed. An exception escaping here would turn a successful
/// human-approved tool call into a tool error the model reads as "it didn't work" — whose correct
/// response is to retry, i.e. perform an approved consequential action a second time. That is
/// worse than a missing audit line. Same shape as <see cref="IEscalationNotificationChannel"/>'s
/// own must-not-throw contract, one layer up: audit and notification failures here are logged and
/// swallowed by the implementation, never surfaced to the caller.
/// </para>
/// <para>
/// This is deliberately not filed under <c>Interfaces/Governance/</c> despite that folder's
/// source-scanned "every registered contract has a caller" guard: that guard's premise is that a
/// contract declared there is load-bearing access control, and this reporter cannot refuse
/// anything — it only describes what already happened. Filing it there to borrow the guard would
/// be exactly the kind of misplacement that guard exists to catch.
/// </para>
/// </remarks>
public interface IApprovalExecutionReporter
{
    /// <summary>Reports that an approved action ran and completed successfully.</summary>
    /// <param name="reportedBy">
    /// A stable identifier for the calling site (<c>"direct-invocation"</c>, <c>"agent-turn"</c>,
    /// or <c>"plan-executor"</c> — the three sites wired to <see cref="IApprovalExecutionReporter"/>
    /// today), carried onto the audit record so a future auditor can tell which raising sites
    /// implement execution reporting and which don't.
    /// </param>
    ValueTask ReportSucceededAsync(ApprovedCall call, string reportedBy, CancellationToken ct);

    /// <summary>Reports that an approved action ran and failed.</summary>
    /// <param name="failureReason">A human-readable reason, shown to the approver.</param>
    /// <param name="reportedBy">See <see cref="ReportSucceededAsync"/>.</param>
    ValueTask ReportFailedAsync(ApprovedCall call, string failureReason, string reportedBy, CancellationToken ct);

    /// <summary>Reports that an approved action never ran.</summary>
    /// <param name="reportedBy">See <see cref="ReportSucceededAsync"/>.</param>
    ValueTask ReportNotExecutedAsync(
        ApprovedCall call, EscalationNotExecutedReason reason, string reportedBy, CancellationToken ct);
}

/// <summary>
/// Identifies one approved call for execution reporting: the escalation that approved it, and the
/// failure-memory key that reporting a failure or success should update.
/// </summary>
/// <param name="EscalationId">The escalation whose approval permitted this call.</param>
/// <param name="Key">
/// The failure-memory key for this call. Carried alongside the escalation id rather than
/// re-derived at report time, because the code path that finally learns the outcome (deep inside
/// a tool invocation) is not always the code path that raised the escalation.
/// </param>
public readonly record struct ApprovedCall(Guid EscalationId, ApprovalFailureKey Key);
