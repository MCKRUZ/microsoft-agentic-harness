using Application.AI.Common.Models.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Models.Tools;

/// <summary>
/// Tests for tool execution model records: <see cref="ToolExecutionProgress"/>,
/// <see cref="ToolExecutionRequest"/>, and <see cref="ToolExecutionResult"/>.
/// Covers construction, required properties, defaults, and record equality.
/// </summary>
public class ToolExecutionModelsTests
{
    [Fact]
    public void ToolExecutionProgress_ConstructsWithRequiredProperties()
    {
        var progress = new ToolExecutionProgress
        {
            CallId = "call-1",
            Status = "executing"
        };

        progress.CallId.Should().Be("call-1");
        progress.Status.Should().Be("executing");
        progress.PercentComplete.Should().BeNull();
    }

    [Fact]
    public void ToolExecutionProgress_WithPercentComplete_SetsValue()
    {
        var progress = new ToolExecutionProgress
        {
            CallId = "call-1",
            Status = "in-progress",
            PercentComplete = 0.75
        };

        progress.PercentComplete.Should().Be(0.75);
    }

    [Fact]
    public void ToolExecutionProgress_Equality_SameValues_AreEqual()
    {
        var a = new ToolExecutionProgress { CallId = "c1", Status = "done" };
        var b = new ToolExecutionProgress { CallId = "c1", Status = "done" };

        a.Should().Be(b);
    }

    [Fact]
    public void ToolExecutionProgress_WithExpression_CreatesModifiedCopy()
    {
        var original = new ToolExecutionProgress { CallId = "c1", Status = "running" };
        var modified = original with { Status = "completed" };

        modified.Status.Should().Be("completed");
        original.Status.Should().Be("running");
    }

    [Fact]
    public void ToolExecutionResult_ConstructsWithRequiredProperties()
    {
        var result = new ToolExecutionResult
        {
            CallId = "call-1",
            Result = ToolResult.Ok("output"),
            Completed = true
        };

        result.CallId.Should().Be("call-1");
        result.Result.Success.Should().BeTrue();
        result.Result.Output.Should().Be("output");
        result.Completed.Should().BeTrue();
        result.ErrorCategory.Should().BeNull();
    }

    [Fact]
    public void ToolExecutionResult_FailedExecution_SetsErrorCategory()
    {
        var result = new ToolExecutionResult
        {
            CallId = "call-2",
            Result = ToolResult.Fail("timed out"),
            Completed = false,
            ErrorCategory = "timeout"
        };

        result.Completed.Should().BeFalse();
        result.Result.Success.Should().BeFalse();
        result.Result.Error.Should().Be("timed out");
        result.ErrorCategory.Should().Be("timeout");
    }

    [Fact]
    public void ToolExecutionResult_Equality_SameValues_AreEqual()
    {
        var toolResult = ToolResult.Ok("ok");
        var a = new ToolExecutionResult { CallId = "c", Result = toolResult, Completed = true };
        var b = new ToolExecutionResult { CallId = "c", Result = toolResult, Completed = true };

        a.Should().Be(b);
    }
}
