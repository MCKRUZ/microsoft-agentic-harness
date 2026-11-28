using Application.AI.Common.Services.Escalation;
using Domain.AI.Escalation;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.Orchestration.Magentic;

/// <summary>
/// Execution reporting for an approved Magentic plan (#395): the approver of a plan review learns how
/// the plan they approved ended.
/// </summary>
/// <remarks>
/// <para>
/// An approval hands the plan back to the workflow, so its outcome is only known later. One approval
/// is open at a time, and it is closed exactly once, at the first of:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>A new plan review.</b> Reported as failed: the approved plan was replaced before the workflow
/// finished with it (MAF asks for a new review when the plan stalls). A replan event alone does not close
/// it — with sign-off required, MAF reviews every replacement plan, and the review is what arrives.
/// </description></item>
/// <item><description>
/// <b>The workflow completing.</b> <c>satisfied</c> is reported as succeeded. Stopping at the round or
/// reset limit is reported as failed: the workflow returns success to its caller in that case, but the
/// plan the approver signed off on did not finish.
/// </description></item>
/// <item><description>
/// <b>The workflow failing.</b> A cancellation is reported as never executed
/// (<see cref="EscalationNotExecutedReason.RunCancelled"/>); any other failure as failed, naming the
/// orchestrator's stable error code.
/// </description></item>
/// </list>
/// <para>
/// Every exit of <see cref="MagenticOrchestrator.RunAsync"/> after the workflow starts goes through
/// <see cref="NotifyCompletedAsync"/> or <see cref="NotifyFailedAsync"/>, so no approval is left open.
/// Failure text here is written by this class; model text never reaches the approver this way.
/// </para>
/// </remarks>
public sealed partial class MagenticEventSubscriber
{
    /// <summary>The <c>reportedBy</c> value on every execution report this subscriber makes.</summary>
    internal const string ApprovalReportedBy = "magentic-plan-review";

    /// <summary>The error code the orchestrator reports when the run was cancelled.</summary>
    internal const string CancelledErrorCode = "magentic.cancelled";

    /// <summary>What the approver is told when a new plan review replaced the plan they approved.</summary>
    internal const string SupersededFailureText =
        "The approved plan did not finish: the workflow stalled and asked for a new plan review.";

    private readonly ScopedApprovalExecutionReporter _approvalReporter;

    /// <summary>The escalation whose approved plan is running, until its outcome is reported.</summary>
    private Guid? _openApproval;

    private Task CloseSupersededApprovalAsync() =>
        CloseApprovalAsync((reporter, id, t) => reporter.ReportFailedAsync(id, SupersededFailureText, ApprovalReportedBy, t));

    private Task CloseApprovalOnCompletionAsync(string completionReason) =>
        completionReason == MagenticConventions.CompletionReasonSatisfied
            ? CloseApprovalAsync((reporter, id, t) => reporter.ReportSucceededAsync(id, ApprovalReportedBy, t))
            : CloseApprovalAsync((reporter, id, t) => reporter.ReportFailedAsync(
                id, $"The workflow stopped before the approved plan finished ({completionReason}).", ApprovalReportedBy, t));

    private Task CloseApprovalOnFailureAsync(string errorCode) =>
        errorCode == CancelledErrorCode
            ? CloseApprovalAsync((reporter, id, t) => reporter.ReportNotExecutedAsync(
                id, EscalationNotExecutedReason.RunCancelled, ApprovalReportedBy, t))
            : CloseApprovalAsync((reporter, id, t) => reporter.ReportFailedAsync(
                id, $"The workflow running the approved plan failed ({errorCode}).", ApprovalReportedBy, t));

    // Bounded like the other terminal reports, and never on the run's token: these go out when the run is
    // ending, often because that token was cancelled. The reporter never throws.
    private async Task CloseApprovalAsync(Func<ScopedApprovalExecutionReporter, Guid, CancellationToken, Task> report)
    {
        if (_openApproval is not { } escalationId)
            return;

        _openApproval = null;
        using var bounded = new CancellationTokenSource(_terminalReportTimeout);
        await report(_approvalReporter, escalationId, bounded.Token).ConfigureAwait(false);
    }
}
