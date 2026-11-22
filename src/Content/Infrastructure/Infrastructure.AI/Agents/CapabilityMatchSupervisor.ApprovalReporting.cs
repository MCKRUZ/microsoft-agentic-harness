using Application.AI.Common.Services.Escalation;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Execution reporting for an approved delegation (#395): once a human approves a delegation the
/// autonomy tier blocked, the approver learns whether the delegation it unblocked actually ran and how
/// it ended, the same loop #325/#366 closed for approved tool calls.
/// </summary>
public sealed partial class CapabilityMatchSupervisor
{
    /// <summary>The <c>reportedBy</c> value on every execution report this supervisor makes.</summary>
    internal const string ApprovalReportedBy = "delegation";

    /// <summary>What the approver is told when an approved delegation ran past its time limit.</summary>
    internal const string ApprovedDelegationTimedOut = "Delegation timed out.";

    private readonly ScopedApprovalExecutionReporter _approvalReporter;

    /// <summary>
    /// Retries a delegation a human approved, at the relaxed <see cref="AutonomyLevel.Restricted"/>
    /// floor, and reports to the approver how it ended. Shared by the tier-1 and tier-2 approval
    /// branches; each passes the escalation whose approval let this run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every way out reports exactly once. A returned attempt reports its own outcome. A cancellation
    /// reports <see cref="EscalationNotExecutedReason.RunCancelled"/> and rethrows, so the caller still
    /// sees the cancellation. Any other exception reports a failure and rethrows into
    /// <see cref="HandleAutonomyEscalationAsync"/>'s fail-closed catch, which already decided what the
    /// caller sees.
    /// </para>
    /// <para>
    /// Reports go out on <see cref="CancellationToken.None"/>, as the tool path's do: the most common
    /// reason to be reporting a cancellation is that <paramref name="ct"/> was cancelled, and reporting
    /// on it would cancel the report before it was written.
    /// </para>
    /// </remarks>
    private async Task<DelegationAttempt> RetryApprovedDelegationAsync(
        Guid escalationId,
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        int currentDelegationDepth,
        IReadOnlyList<string>? toolOverrides,
        CancellationToken ct)
    {
        DelegationAttempt attempt;
        try
        {
            // +1: an approved-escalation retry is another attempt at the same delegation, not a
            // fresh call — without advancing depth here, an approver who keeps approving (or a
            // misconfigured selector that never finds a match) could retry indefinitely with
            // MaxDelegationDepth never tripping.
            attempt = await DelegateCoreAsync(
                taskDescription, requiredCapabilities, AutonomyLevel.Restricted,
                currentDelegationDepth + 1, toolOverrides, ct);
        }
        catch (OperationCanceledException)
        {
            await _approvalReporter.ReportNotExecutedAsync(
                escalationId, EscalationNotExecutedReason.RunCancelled, ApprovalReportedBy, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _approvalReporter.ReportFailedAsync(
                escalationId, SafeFailureText.For("Delegation failed", ex), ApprovalReportedBy, CancellationToken.None);
            throw;
        }

        await ReportApprovedAttemptAsync(escalationId, attempt);
        return attempt;
    }

    private Task ReportApprovedAttemptAsync(Guid escalationId, DelegationAttempt attempt)
    {
        _logger.LogInformation(
            "Approved delegation for escalation {EscalationId} ended as {Outcome}", escalationId, attempt.Outcome);

        return attempt.Outcome switch
        {
            DelegationOutcome.Succeeded => _approvalReporter.ReportSucceededAsync(
                escalationId, ApprovalReportedBy, CancellationToken.None),
            DelegationOutcome.Failed => _approvalReporter.ReportFailedAsync(
                escalationId, attempt.FailureText!, ApprovalReportedBy, CancellationToken.None),
            DelegationOutcome.Cancelled => _approvalReporter.ReportNotExecutedAsync(
                escalationId, EscalationNotExecutedReason.RunCancelled, ApprovalReportedBy, CancellationToken.None),
            DelegationOutcome.NotStarted => _approvalReporter.ReportNotExecutedAsync(
                escalationId, attempt.NotStartedReason!.Value, ApprovalReportedBy, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unhandled delegation outcome {attempt.Outcome}.")
        };
    }

    /// <summary>How a delegation ended, as distinct from the <see cref="DelegationResult"/> its caller sees.</summary>
    private enum DelegationOutcome
    {
        /// <summary>Nothing ran: refused before an agent was started.</summary>
        NotStarted,

        /// <summary>The caller cancelled it while it was running.</summary>
        Cancelled,

        /// <summary>It ran and failed, threw, or ran past its time limit.</summary>
        Failed,

        /// <summary>It ran and completed.</summary>
        Succeeded
    }

    /// <summary>
    /// A delegation's result for its caller, plus what actually happened. The caller's
    /// <see cref="DelegationResult"/> uses <c>IsSuccess = false</c> for a delegation that never started
    /// and for one that ran and failed; an approver must be told which, so the outcome is carried
    /// separately rather than read back out of the result's failure text.
    /// </summary>
    /// <param name="Result">What the caller sees.</param>
    /// <param name="Outcome">What happened.</param>
    /// <param name="NotStartedReason">Set only for <see cref="DelegationOutcome.NotStarted"/>.</param>
    /// <param name="FailureText">
    /// Set only for <see cref="DelegationOutcome.Failed"/>. Written by this class, never model output:
    /// it reaches the approver untreated.
    /// </param>
    private readonly record struct DelegationAttempt(
        DelegationResult Result,
        DelegationOutcome Outcome,
        EscalationNotExecutedReason? NotStartedReason = null,
        string? FailureText = null)
    {
        public static DelegationAttempt NotStarted(DelegationResult result, EscalationNotExecutedReason reason) =>
            new(result, DelegationOutcome.NotStarted, NotStartedReason: reason);

        public static DelegationAttempt Cancelled(DelegationResult result) =>
            new(result, DelegationOutcome.Cancelled);

        public static DelegationAttempt Failed(DelegationResult result, string failureText) =>
            new(result, DelegationOutcome.Failed, FailureText: failureText);

        public static DelegationAttempt Succeeded(DelegationResult result) =>
            new(result, DelegationOutcome.Succeeded);
    }
}
