using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Escalation;

/// <summary>
/// Reports an approved action's outcome from a long-lived service that cannot take the scoped
/// <see cref="IApprovalExecutionReporter"/> as a dependency (#395): opens a scope per report,
/// resolves the reporter there, and reports.
/// </summary>
/// <remarks>
/// <para>
/// Used by the two raising sites that run something after approval but live outside any request
/// scope of their own — <c>CapabilityMatchSupervisor</c> (delegation) and
/// <c>MagenticEventSubscriber</c> (plan review, built by a singleton orchestrator). Both report with
/// no failure-memory key: see <see cref="ApprovedCall.Key"/> for why.
/// </para>
/// <para>
/// <strong>Never throws</strong>, the same contract as <see cref="IApprovalExecutionReporter"/>, and
/// it has to hold here too: the reporter's own guard only covers what happens inside it. Opening the
/// scope and resolving the reporter happen before that guard — they fail on a disposed provider at
/// shutdown, or a host that never registered the reporter — and an exception from either would turn an
/// approved delegation that succeeded into an error its caller may retry.
/// </para>
/// <para>
/// A fresh scope still reaches the approver: the AG-UI notifier finds its writer through an
/// <c>AsyncLocal</c>, which flows into a new scope on the same async path.
/// </para>
/// </remarks>
public sealed class ScopedApprovalExecutionReporter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>Creates the reporter.</summary>
    /// <param name="scopeFactory">Opens the scope each report resolves the reporter from.</param>
    /// <param name="logger">The owning service's logger, so a failed report is logged under the site that made it.</param>
    public ScopedApprovalExecutionReporter(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Reports that the approved action ran and completed.</summary>
    /// <param name="escalationId">The escalation whose approval permitted the action.</param>
    /// <param name="reportedBy">The calling site; see <see cref="IApprovalExecutionReporter.ReportSucceededAsync"/>.</param>
    /// <param name="ct">Bounds the report itself, not the action.</param>
    public Task ReportSucceededAsync(Guid escalationId, string reportedBy, CancellationToken ct) =>
        ReportAsync(escalationId, (reporter, call) => reporter.ReportSucceededAsync(call, reportedBy, ct));

    /// <summary>Reports that the approved action ran and failed.</summary>
    /// <param name="escalationId">The escalation whose approval permitted the action.</param>
    /// <param name="fixedFailureText">
    /// Text the calling site wrote, shown to the approver as it is. It is not sanitized or redacted, so
    /// it must never carry model output, tool output, or exception messages — an exception's type name
    /// (<see cref="SafeFailureText"/>) is the most that may be added.
    /// </param>
    /// <param name="reportedBy">The calling site; see <see cref="IApprovalExecutionReporter.ReportSucceededAsync"/>.</param>
    /// <param name="ct">Bounds the report itself, not the action.</param>
    public Task ReportFailedAsync(Guid escalationId, string fixedFailureText, string reportedBy, CancellationToken ct) =>
        ReportAsync(escalationId, (reporter, call) => reporter.ReportFailedAsync(
            call, new PreparedFailureText(fixedFailureText, FailureTextSubstitution.None), reportedBy, ct));

    /// <summary>Reports that the approved action never ran.</summary>
    /// <param name="escalationId">The escalation whose approval permitted the action.</param>
    /// <param name="reason">Why it never ran.</param>
    /// <param name="reportedBy">The calling site; see <see cref="IApprovalExecutionReporter.ReportSucceededAsync"/>.</param>
    /// <param name="ct">Bounds the report itself, not the action.</param>
    public Task ReportNotExecutedAsync(
        Guid escalationId, EscalationNotExecutedReason reason, string reportedBy, CancellationToken ct) =>
        ReportAsync(escalationId, (reporter, call) => reporter.ReportNotExecutedAsync(call, reason, reportedBy, ct));

    private async Task ReportAsync(Guid escalationId, Func<IApprovalExecutionReporter, ApprovedCall, ValueTask> report)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reporter = scope.ServiceProvider.GetService<IApprovalExecutionReporter>();
            if (reporter is null)
            {
                _logger.LogWarning(
                    "No IApprovalExecutionReporter is registered, so the outcome of approved escalation " +
                    "{EscalationId} was not reported — the approver will not learn what it did.",
                    escalationId);
                return;
            }

            await report(reporter, new ApprovedCall(escalationId, Key: null)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to report the outcome of approved escalation {EscalationId} — the approver may " +
                "never learn what this approved action did.",
                escalationId);
        }
    }
}
