using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// Tests for <see cref="StructuredLogAuditSink"/> covering log output formatting
/// for success, failure, and system-level entries, and the correlation id and metadata a record's detail lives in (#900).
/// </summary>
public sealed class StructuredLogAuditSinkTests
{
    private readonly Mock<ILogger<StructuredLogAuditSink>> _logger = new();
    private readonly StructuredLogAuditSink _sut;

    public StructuredLogAuditSinkTests()
    {
        _sut = new StructuredLogAuditSink(_logger.Object);
    }

    [Fact]
    public async Task RecordAsync_SuccessEntry_LogsInformation()
    {
        var entry = new AuditEntry
        {
            Action = "FileWrite",
            RequestType = "WriteCommand",
            ExecutorId = "agent-1",
            Outcome = AuditOutcome.Success,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _sut.RecordAsync(entry, CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("FileWrite")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordAsync_FailureWithReason_IncludesReasonInLog()
    {
        var entry = new AuditEntry
        {
            Action = "ToolExecution",
            RequestType = "ExecCommand",
            ExecutorId = "agent-2",
            Outcome = AuditOutcome.Failure,
            FailureReason = "Timeout exceeded",
            Timestamp = DateTimeOffset.UtcNow
        };

        await _sut.RecordAsync(entry, CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Timeout exceeded")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordAsync_NullExecutor_LogsAsSystem()
    {
        var entry = new AuditEntry
        {
            Action = "Startup",
            RequestType = "InitCommand",
            ExecutorId = null,
            Outcome = AuditOutcome.Success,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _sut.RecordAsync(entry, CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("system")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordAsync_DeniedEntry_LogsOutcome()
    {
        var entry = new AuditEntry
        {
            Action = "DangerousOp",
            RequestType = "DeleteCommand",
            ExecutorId = "rogue-agent",
            Outcome = AuditOutcome.Denied,
            FailureReason = "Insufficient permissions",
            Timestamp = DateTimeOffset.UtcNow
        };

        await _sut.RecordAsync(entry, CancellationToken.None);

        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Denied")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordAsync_ReturnsCompletedValueTask()
    {
        var entry = new AuditEntry
        {
            Action = "Test",
            RequestType = "TestCmd",
            Outcome = AuditOutcome.Success,
            Timestamp = DateTimeOffset.UtcNow
        };

        var task = _sut.RecordAsync(entry, CancellationToken.None);

        task.IsCompleted.Should().BeTrue();
        await task;
    }

    [Fact]
    public async Task RecordAsync_WritesCorrelationIdAndEveryMetadataPair()
    {
        // Without these, the erasure record lost its subject reference, per-store counts and the phase pairing an attempt
        // with its outcome, and two identical lines proved nothing.
        var logger = new CapturingLogger<StructuredLogAuditSink>();

        await new StructuredLogAuditSink(logger).RecordAsync(new AuditEntry
        {
            RequestType = "EraseSubjectDataCommand",
            Action = "right_to_erasure",
            ExecutorId = "operator-9",
            CorrelationId = "corr-123",
            Timestamp = DateTimeOffset.UnixEpoch,
            Outcome = AuditOutcome.Success,
            Metadata = new Dictionary<string, string>
            {
                ["phase"] = "outcome",
                ["subject_reference"] = "ab12cd",
                ["completeness"] = "Partial",
                ["store.conversations"] = "Erased:5",
            },
        }, CancellationToken.None);

        var line = logger.Messages.Should().ContainSingle().Subject;
        line.Should().Contain("corr-123").And.Contain("operator-9").And.Contain("Success");
        line.Should().Contain("phase=outcome").And.Contain("subject_reference=ab12cd")
            .And.Contain("completeness=Partial").And.Contain("store.conversations=Erased:5");
    }

    [Fact]
    public async Task RecordAsync_ValueWithDelimitersOrLineBreaks_CannotForgeAFieldOrALine()
    {
        var logger = new CapturingLogger<StructuredLogAuditSink>();

        await new StructuredLogAuditSink(logger).RecordAsync(new AuditEntry
        {
            RequestType = "SomeCommand",
            Action = "something",
            Timestamp = DateTimeOffset.UnixEpoch,
            Outcome = AuditOutcome.Success,
            Metadata = new Dictionary<string, string> { ["note"] = "x; completeness=Full\nAudit: forged" },
        }, CancellationToken.None);

        var line = logger.Messages.Should().ContainSingle().Subject;
        line.Should().NotContain("completeness=Full").And.NotContain("\n");
        line.Should().Contain("note=x%3B completeness%3DFull%0AAudit: forged");
    }
}
