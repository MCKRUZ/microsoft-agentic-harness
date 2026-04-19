using Domain.Common.Models;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Models;

/// <summary>
/// Tests for <see cref="AuditEntry"/> record and <see cref="AuditOutcome"/> enum.
/// </summary>
public class AuditEntryTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new AuditEntry
        {
            RequestType = "CreateOrderCommand",
            Action = "FileWrite",
            Timestamp = timestamp,
            Outcome = AuditOutcome.Success
        };

        entry.RequestType.Should().Be("CreateOrderCommand");
        entry.Action.Should().Be("FileWrite");
        entry.Timestamp.Should().Be(timestamp);
        entry.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public void OptionalProperties_DefaultToNull()
    {
        var entry = new AuditEntry
        {
            RequestType = "Test",
            Action = "Read",
            Timestamp = DateTimeOffset.UtcNow,
            Outcome = AuditOutcome.Success
        };

        entry.ExecutorId.Should().BeNull();
        entry.CorrelationId.Should().BeNull();
        entry.StepNumber.Should().BeNull();
        entry.FailureReason.Should().BeNull();
        entry.Metadata.Should().BeNull();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var metadata = new Dictionary<string, string> { ["path"] = "/tmp/file.txt" };
        var entry = new AuditEntry
        {
            RequestType = "WriteFileCommand",
            Action = "FileWrite",
            ExecutorId = "agent-1",
            CorrelationId = "corr-123",
            StepNumber = 5,
            Timestamp = DateTimeOffset.UtcNow,
            Outcome = AuditOutcome.Failure,
            FailureReason = "Permission denied",
            Metadata = metadata
        };

        entry.ExecutorId.Should().Be("agent-1");
        entry.StepNumber.Should().Be(5);
        entry.FailureReason.Should().Be("Permission denied");
        entry.Metadata.Should().ContainKey("path");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new AuditEntry
        {
            RequestType = "Test",
            Action = "Read",
            Timestamp = DateTimeOffset.UtcNow,
            Outcome = AuditOutcome.Success
        };

        var modified = original with { Outcome = AuditOutcome.Denied };

        original.Outcome.Should().Be(AuditOutcome.Success);
        modified.Outcome.Should().Be(AuditOutcome.Denied);
    }
}

/// <summary>
/// Tests for <see cref="AuditOutcome"/> enum values.
/// </summary>
public class AuditOutcomeTests
{
    [Theory]
    [InlineData(AuditOutcome.Success, 0)]
    [InlineData(AuditOutcome.Failure, 1)]
    [InlineData(AuditOutcome.Denied, 2)]
    public void Value_HasExpectedInteger(AuditOutcome outcome, int expected)
    {
        ((int)outcome).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        var values = Enum.GetValues<AuditOutcome>();

        values.Should().OnlyHaveUniqueItems();
        values.Should().HaveCount(3);
    }
}
