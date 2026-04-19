using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="ExecutionTraceRecord"/>, <see cref="TraceRecordTypes"/>,
/// and <see cref="TraceResultCategories"/> constants.
/// </summary>
public class ExecutionTraceRecordTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var record = new ExecutionTraceRecord();

        record.Seq.Should().Be(0);
        record.Type.Should().BeEmpty();
        record.ExecutionRunId.Should().BeEmpty();
        record.CandidateId.Should().BeNull();
        record.Iteration.Should().BeNull();
        record.TaskId.Should().BeNull();
        record.TurnId.Should().BeEmpty();
        record.ToolName.Should().BeNull();
        record.ResultCategory.Should().BeNull();
        record.PayloadSummary.Should().BeNull();
        record.PayloadFullPath.Should().BeNull();
        record.Redacted.Should().BeNull();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var ts = DateTimeOffset.UtcNow;
        var record = new ExecutionTraceRecord
        {
            Seq = 1,
            Ts = ts,
            Type = TraceRecordTypes.ToolCall,
            ExecutionRunId = "run-1",
            CandidateId = "cand-1",
            Iteration = 2,
            TaskId = "task-1",
            TurnId = "turn-1",
            ToolName = "read_file",
            ResultCategory = TraceResultCategories.Success,
            PayloadSummary = "Read /path/to/file",
            PayloadFullPath = "/traces/payload.json",
            Redacted = false
        };

        record.Seq.Should().Be(1);
        record.Ts.Should().Be(ts);
        record.Type.Should().Be("tool_call");
        record.ToolName.Should().Be("read_file");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new ExecutionTraceRecord { Seq = 1, TurnId = "t1" };

        var modified = original with { Seq = 2 };

        original.Seq.Should().Be(1);
        modified.Seq.Should().Be(2);
    }
}

/// <summary>
/// Tests for <see cref="TraceRecordTypes"/> constants.
/// </summary>
public class TraceRecordTypesTests
{
    [Fact]
    public void ToolCall_HasExpectedValue()
    {
        TraceRecordTypes.ToolCall.Should().Be("tool_call");
    }

    [Fact]
    public void ToolResult_HasExpectedValue()
    {
        TraceRecordTypes.ToolResult.Should().Be("tool_result");
    }

    [Fact]
    public void Decision_HasExpectedValue()
    {
        TraceRecordTypes.Decision.Should().Be("decision");
    }

    [Fact]
    public void Observation_HasExpectedValue()
    {
        TraceRecordTypes.Observation.Should().Be("observation");
    }
}

/// <summary>
/// Tests for <see cref="TraceResultCategories"/> constants.
/// </summary>
public class TraceResultCategoriesTests
{
    [Fact]
    public void Success_HasExpectedValue()
    {
        TraceResultCategories.Success.Should().Be("success");
    }

    [Fact]
    public void Partial_HasExpectedValue()
    {
        TraceResultCategories.Partial.Should().Be("partial");
    }

    [Fact]
    public void Error_HasExpectedValue()
    {
        TraceResultCategories.Error.Should().Be("error");
    }

    [Fact]
    public void Timeout_HasExpectedValue()
    {
        TraceResultCategories.Timeout.Should().Be("timeout");
    }

    [Fact]
    public void Blocked_HasExpectedValue()
    {
        TraceResultCategories.Blocked.Should().Be("blocked");
    }
}
