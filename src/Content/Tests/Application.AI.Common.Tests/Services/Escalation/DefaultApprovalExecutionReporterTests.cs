using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Services.Escalation;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Escalation;

/// <summary>
/// Tests for <see cref="DefaultApprovalExecutionReporter"/> — the #325 execution-reporting path,
/// whose two defining properties are the audit-then-notify-then-memory ordering and a
/// must-not-throw contract that holds regardless of which dependency fails.
/// </summary>
public sealed class DefaultApprovalExecutionReporterTests
{
    private readonly Mock<IEscalationAuditStore> _auditStore = new();
    private readonly Mock<IEscalationNotifier> _notifier = new();
    private readonly Mock<IApprovalFailureMemory> _failureMemory = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private DefaultApprovalExecutionReporter Create() => new(
        _auditStore.Object, _notifier.Object, _failureMemory.Object, _timeProvider,
        NullLogger<DefaultApprovalExecutionReporter>.Instance);

    private static ApprovedCall Call() =>
        new(Guid.NewGuid(), new ApprovalFailureKey("conv-1", "agent-1", "file_system"));

    [Fact]
    public async Task ReportSucceededAsync_WritesAuditBeforeNotify()
    {
        var order = new List<string>();
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("audit")).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyExecutionReportedAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("notify")).Returns(Task.CompletedTask);

        var call = Call();
        await Create().ReportSucceededAsync(call, "tool-invocation", CancellationToken.None);

        Assert.Equal(["audit", "notify"], order);
        _failureMemory.Verify(m => m.Clear(call.Key), Times.Once);
    }

    [Fact]
    public async Task ReportSucceededAsync_WritesTheExpectedAuditRecord()
    {
        EscalationExecutionRecord? recorded = null;
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationExecutionRecord, CancellationToken>((r, _) => recorded = r)
            .Returns(Task.CompletedTask);

        var call = Call();
        await Create().ReportSucceededAsync(call, "tool-invocation", CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(call.EscalationId, recorded!.EscalationId);
        Assert.Equal(EscalationExecutionStatus.Succeeded, recorded.Status);
        Assert.Null(recorded.FailureReason);
        Assert.Equal("tool-invocation", recorded.ReportedBy);
    }

    [Fact]
    public async Task ReportFailedAsync_RecordsFailureAgainstMemory_NotClear()
    {
        var call = Call();
        await Create().ReportFailedAsync(call, "permission denied", "tool-invocation", CancellationToken.None);

        _failureMemory.Verify(
            m => m.RecordFailure(call.Key, "permission denied", call.EscalationId), Times.Once);
        _failureMemory.Verify(m => m.Clear(It.IsAny<ApprovalFailureKey>()), Times.Never);
    }

    [Fact]
    public async Task ReportFailedAsync_WritesTheExpectedAuditRecord()
    {
        EscalationExecutionRecord? recorded = null;
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationExecutionRecord, CancellationToken>((r, _) => recorded = r)
            .Returns(Task.CompletedTask);

        var call = Call();
        await Create().ReportFailedAsync(call, "permission denied", "tool-invocation", CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(EscalationExecutionStatus.Failed, recorded!.Status);
        Assert.Equal("permission denied", recorded.FailureReason);
    }

    [Fact]
    public async Task ReportNotExecutedAsync_TouchesNeitherClearNorRecordFailure()
    {
        // Nothing ran, so there is no new failure to cite and any prior sequence must stay live —
        // neither cleared nor recorded as a further failure.
        var call = Call();
        await Create().ReportNotExecutedAsync(
            call, EscalationNotExecutedReason.RunCancelled, "plan-executor", CancellationToken.None);

        _failureMemory.Verify(m => m.Clear(It.IsAny<ApprovalFailureKey>()), Times.Never);
        _failureMemory.Verify(
            m => m.RecordFailure(It.IsAny<ApprovalFailureKey>(), It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ReportNotExecutedAsync_WritesTheExpectedAuditRecord()
    {
        EscalationExecutionRecord? recorded = null;
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationExecutionRecord, CancellationToken>((r, _) => recorded = r)
            .Returns(Task.CompletedTask);

        var call = Call();
        await Create().ReportNotExecutedAsync(
            call, EscalationNotExecutedReason.RunCancelled, "plan-executor", CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(EscalationExecutionStatus.NeverExecuted, recorded!.Status);
        Assert.Equal(EscalationNotExecutedReason.RunCancelled, recorded.NotExecutedReason);
    }

    // ===== Must-not-throw contract: a failure in any single dependency must not escape =====

    [Fact]
    public async Task ReportSucceededAsync_AuditStoreThrows_DoesNotThrow()
    {
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk full"));

        var exception = await Record.ExceptionAsync(
            () => Create().ReportSucceededAsync(Call(), "tool-invocation", CancellationToken.None).AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReportSucceededAsync_NotifierThrows_DoesNotThrow()
    {
        _notifier.Setup(n => n.NotifyExecutionReportedAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel unreachable"));

        var exception = await Record.ExceptionAsync(
            () => Create().ReportSucceededAsync(Call(), "tool-invocation", CancellationToken.None).AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReportSucceededAsync_NotifierThrows_MemoryIsNeverUpdated()
    {
        // Ordering's other half: a failed notify must not let a stale memory update slip through
        // behind it, because the notify failure means the approver never received this report and
        // clearing "solves" a loop that, from the approver's side, is still open.
        _notifier.Setup(n => n.NotifyExecutionReportedAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel unreachable"));

        await Create().ReportSucceededAsync(Call(), "tool-invocation", CancellationToken.None);

        _failureMemory.Verify(m => m.Clear(It.IsAny<ApprovalFailureKey>()), Times.Never);
    }

    [Fact]
    public async Task ReportFailedAsync_MemoryThrows_DoesNotThrow()
    {
        _failureMemory
            .Setup(m => m.RecordFailure(It.IsAny<ApprovalFailureKey>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new InvalidOperationException("memory corrupted"));

        var exception = await Record.ExceptionAsync(
            () => Create().ReportFailedAsync(Call(), "boom", "tool-invocation", CancellationToken.None).AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReportFailedAsync_OperationCanceled_MutationControl_DoesNotThrow()
    {
        // The turn was abandoned while the report was in flight. This is not the caller's fault and
        // must not surface as an exception any more than a genuine infrastructure fault does.
        var cts = new CancellationTokenSource();
        _auditStore.Setup(s => s.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        cts.Cancel();

        var exception = await Record.ExceptionAsync(
            () => Create().ReportFailedAsync(Call(), "boom", "tool-invocation", cts.Token).AsTask());

        Assert.Null(exception);
    }
}
