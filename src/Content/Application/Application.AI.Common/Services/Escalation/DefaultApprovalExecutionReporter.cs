using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Escalation;

/// <summary>Default <see cref="IApprovalExecutionReporter"/>: audits, notifies, then updates failure memory.</summary>
/// <remarks>
/// <strong>Ordering is deliberate and load-bearing.</strong> Audit before notify, so a
/// notification-channel outage never produces an approver-visible report with no audit line
/// behind it. Failure memory last, so a failed audit write never leaves memory claiming an
/// attempt the audit trail cannot corroborate.
/// <para>
/// Every step runs inside one try/catch per this type's own must-not-throw contract (see
/// <see cref="IApprovalExecutionReporter"/>). The reachable exception set spans a file-backed
/// hash-chain writer and four notification channels — not enumerable here, same reasoning as
/// <c>EscalationToolApprovalRouter.Render</c>'s catch-all.
/// </para>
/// </remarks>
public sealed class DefaultApprovalExecutionReporter : IApprovalExecutionReporter
{
    private readonly IEscalationAuditStore _auditStore;
    private readonly IEscalationNotifier _notifier;
    private readonly IApprovalFailureMemory _failureMemory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultApprovalExecutionReporter> _logger;

    /// <summary>Creates the reporter.</summary>
    public DefaultApprovalExecutionReporter(
        IEscalationAuditStore auditStore,
        IEscalationNotifier notifier,
        IApprovalFailureMemory failureMemory,
        TimeProvider timeProvider,
        ILogger<DefaultApprovalExecutionReporter> logger)
    {
        _auditStore = auditStore;
        _notifier = notifier;
        _failureMemory = failureMemory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask ReportSucceededAsync(ApprovedCall call, string reportedBy, CancellationToken ct) =>
        ReportAsync(
            call,
            () => EscalationExecutionRecord.Succeeded(call.EscalationId, _timeProvider.GetUtcNow(), reportedBy),
            onRecorded: () => _failureMemory.Clear(call.Key),
            ct);

    /// <inheritdoc />
    public ValueTask ReportFailedAsync(ApprovedCall call, string failureReason, string reportedBy, CancellationToken ct) =>
        ReportAsync(
            call,
            () => EscalationExecutionRecord.Failed(call.EscalationId, failureReason, _timeProvider.GetUtcNow(), reportedBy),
            onRecorded: () => _failureMemory.RecordFailure(call.Key, failureReason, call.EscalationId),
            ct);

    /// <inheritdoc />
    public ValueTask ReportNotExecutedAsync(
        ApprovedCall call, EscalationNotExecutedReason reason, string reportedBy, CancellationToken ct) =>
        ReportAsync(
            call,
            () => EscalationExecutionRecord.NeverExecuted(call.EscalationId, reason, _timeProvider.GetUtcNow(), reportedBy),
            // Nothing ran, so there is no new failure to cite and the prior sequence (if any) is
            // still live — neither cleared nor recorded as a further failure.
            onRecorded: null,
            ct);

    /// <summary>
    /// Builds and reports the record. <paramref name="buildRecord"/> is a factory rather than an
    /// already-built record so a caller-supplied bad value (a blank failure reason) throws
    /// <em>inside</em> this method's try/catch rather than while the caller is still evaluating
    /// arguments — the whole point of this type is that nothing it does escapes as an exception.
    /// </summary>
    private async ValueTask ReportAsync(
        ApprovedCall call, Func<EscalationExecutionRecord> buildRecord, Action? onRecorded, CancellationToken ct)
    {
        try
        {
            var record = buildRecord();
            await _auditStore.RecordExecutionAsync(record, ct).ConfigureAwait(false);
            await _notifier.NotifyExecutionReportedAsync(record, ct).ConfigureAwait(false);
            onRecorded?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Execution report for escalation {EscalationId} was cancelled with the turn.",
                call.EscalationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to report execution result for escalation {EscalationId} — the approver may " +
                "never learn what this approved action did.",
                call.EscalationId);
        }
    }
}
