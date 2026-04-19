using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="EvalTask"/> record — construction, defaults,
/// and record immutability.
/// </summary>
public class EvalTaskTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var task = new EvalTask
        {
            TaskId = "task-01",
            Description = "Test the agent",
            InputPrompt = "What is 2+2?"
        };

        task.TaskId.Should().Be("task-01");
        task.Description.Should().Be("Test the agent");
        task.InputPrompt.Should().Be("What is 2+2?");
    }

    [Fact]
    public void OptionalProperties_DefaultCorrectly()
    {
        var task = new EvalTask
        {
            TaskId = "t1",
            Description = "d",
            InputPrompt = "p"
        };

        task.ExpectedOutputPattern.Should().BeNull();
        task.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Tags_CanBeInitialized()
    {
        var task = new EvalTask
        {
            TaskId = "t1",
            Description = "d",
            InputPrompt = "p",
            Tags = ["smoke", "regression"]
        };

        task.Tags.Should().HaveCount(2);
        task.Tags.Should().Contain("smoke");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new EvalTask
        {
            TaskId = "t1",
            Description = "d",
            InputPrompt = "p"
        };

        var modified = original with { TaskId = "t2" };

        original.TaskId.Should().Be("t1");
        modified.TaskId.Should().Be("t2");
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new EvalTask { TaskId = "t1", Description = "d", InputPrompt = "p" };
        var b = new EvalTask { TaskId = "t1", Description = "d", InputPrompt = "p" };

        a.Should().Be(b);
    }
}
