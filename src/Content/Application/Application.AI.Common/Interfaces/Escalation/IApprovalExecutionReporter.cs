using Application.AI.Common.Services.Tools;
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
    /// <c>"plan-executor"</c>, <c>"delegation"</c>, or <c>"magentic-plan-review"</c> — the five sites
    /// wired to <see cref="IApprovalExecutionReporter"/> today), carried onto the audit record so a
    /// future auditor can tell which raising sites implement execution reporting and which don't.
    /// </param>
    ValueTask ReportSucceededAsync(ApprovedCall call, string reportedBy, CancellationToken ct);

    /// <summary>Reports that an approved action ran and failed.</summary>
    /// <param name="failureReason">
    /// The text to show the approver, plus why it is a substitute when it is one (#472) — a sanitizer
    /// that removed everything and a tool that genuinely emitted the resulting placeholder string used
    /// to be indistinguishable at this parameter's old <c>string</c> type.
    /// </param>
    /// <param name="reportedBy">See <see cref="ReportSucceededAsync"/>.</param>
    ValueTask ReportFailedAsync(ApprovedCall call, PreparedFailureText failureReason, string reportedBy, CancellationToken ct);

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
/// The failure-memory key for this call, or null when the approved action has no tool call that
/// failure memory could ever be consulted for. Carried alongside the escalation id rather than
/// re-derived at report time, because the code path that finally learns the outcome (deep inside
/// a tool invocation) is not always the code path that raised the escalation.
/// <para>
/// Null for a delegation or a Magentic plan review (#395): the only reader of failure memory,
/// <c>EscalationToolApprovalRouter</c>, looks keys up by the name of a real tool about to be
/// approved, so a key built from <c>delegate:…</c> or <c>magentic.plan_review</c> would never be
/// read — it would only take a slot in the bounded memory and push out an entry that is.
/// </para>
/// </param>
public readonly record struct ApprovedCall(Guid EscalationId, ApprovalFailureKey? Key);
