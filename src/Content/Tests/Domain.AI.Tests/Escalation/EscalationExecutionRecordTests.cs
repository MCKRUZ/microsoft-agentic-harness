using Domain.AI.Escalation;
using Xunit;

namespace Domain.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="EscalationExecutionRecord"/>'s factories — the shape that makes a failure
/// with no reason, or a never-executed record with no reason, unconstructable rather than merely
/// discouraged.
/// </summary>
public sealed class EscalationExecutionRecordTests
{
    private static readonly Guid EscalationId = Guid.NewGuid();
    private static readonly DateTimeOffset ReportedAt = DateTimeOffset.UtcNow;
    private const string ReportedBy = "tool-invocation";

    [Fact]
    public void Succeeded_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.Succeeded(EscalationId, ReportedAt, ReportedBy);

        Assert.Equal(EscalationId, record.EscalationId);
        Assert.Equal(EscalationExecutionStatus.Succeeded, record.Status);
        Assert.Null(record.FailureReason);
        Assert.Null(record.NotExecutedReason);
        Assert.Equal(ReportedAt, record.ReportedAt);
        Assert.Equal(ReportedBy, record.ReportedBy);
    }

    [Fact]
    public void Failed_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.Failed(EscalationId, "permission denied", ReportedAt, ReportedBy);

        Assert.Equal(EscalationExecutionStatus.Failed, record.Status);
        Assert.Equal("permission denied", record.FailureReason);
        Assert.Null(record.NotExecutedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Failed_BlankFailureReason_Throws(string? failureReason)
    {
        // The whole point of the private constructor: a Failed record with nothing an approver can
        // read is indistinguishable from success, so this shape must never exist rather than be
        // guarded against at every reader.
        Assert.ThrowsAny<ArgumentException>(
            () => EscalationExecutionRecord.Failed(EscalationId, failureReason!, ReportedAt, ReportedBy));
    }

    [Fact]
    public void Failed_NonBlankFailureReason_MutationControl_DoesNotThrow()
    {
        var exception = Record.Exception(
            () => EscalationExecutionRecord.Failed(EscalationId, "boom", ReportedAt, ReportedBy));

        Assert.Null(exception);
    }

    [Fact]
    public void NeverExecuted_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.NeverExecuted(
            EscalationId, EscalationNotExecutedReason.RunCancelled, ReportedAt, ReportedBy);

        Assert.Equal(EscalationExecutionStatus.NeverExecuted, record.Status);
        Assert.Null(record.FailureReason);
        Assert.Equal(EscalationNotExecutedReason.RunCancelled, record.NotExecutedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Succeeded_BlankReportedBy_Throws(string? reportedBy)
    {
        // ReportedBy is what makes "nobody reported" distinguishable from "this site reported" when
        // auditing which raising sites implement execution reporting — a blank value defeats that.
        Assert.ThrowsAny<ArgumentException>(
            () => EscalationExecutionRecord.Succeeded(EscalationId, ReportedAt, reportedBy!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NeverExecuted_BlankReportedBy_Throws(string? reportedBy)
    {
        Assert.ThrowsAny<ArgumentException>(() => EscalationExecutionRecord.NeverExecuted(
            EscalationId, EscalationNotExecutedReason.RunCancelled, ReportedAt, reportedBy!));
    }
}
