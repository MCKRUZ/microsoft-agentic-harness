using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="NodeState"/> — defaults, status checks, metadata,
/// duration calculation, and iteration tracking.
/// </summary>
public class NodeStateTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var node = new NodeState();

        node.NodeId.Should().BeEmpty();
        node.NodeType.Should().Be("skill");
        node.Status.Should().Be("not_started");
        node.StartedAt.Should().BeNull();
        node.CompletedAt.Should().BeNull();
        node.Iteration.Should().Be(1);
        node.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void IsComplete_WithCompleted_ReturnsTrue()
    {
        var node = new NodeState { Status = "completed" };

        node.IsComplete().Should().BeTrue();
    }

    [Fact]
    public void IsComplete_WithOther_ReturnsFalse()
    {
        var node = new NodeState { Status = "in_progress" };

        node.IsComplete().Should().BeFalse();
    }

    [Theory]
    [InlineData("in_progress", true)]
    [InlineData("awaiting_input", true)]
    [InlineData("awaiting_approval", true)]
    [InlineData("not_started", false)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    public void IsActive_ReturnsExpected(string status, bool expected)
    {
        var node = new NodeState { Status = status };

        node.IsActive().Should().Be(expected);
    }

    [Fact]
    public void HasFailed_WithFailed_ReturnsTrue()
    {
        var node = new NodeState { Status = "failed" };

        node.HasFailed().Should().BeTrue();
    }

    [Fact]
    public void HasFailed_WithOther_ReturnsFalse()
    {
        var node = new NodeState { Status = "completed" };

        node.HasFailed().Should().BeFalse();
    }

    [Fact]
    public void GetDuration_WithNoStartedAt_ReturnsNull()
    {
        var node = new NodeState();

        node.GetDuration().Should().BeNull();
    }

    [Fact]
    public void GetDuration_WithStartAndComplete_ReturnsDifference()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(5);
        var node = new NodeState { StartedAt = start, CompletedAt = end };

        node.GetDuration().Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void GetDuration_WithStartButNoComplete_UsesUtcNow()
    {
        var node = new NodeState { StartedAt = DateTime.UtcNow.AddSeconds(-10) };

        var duration = node.GetDuration();

        duration.Should().NotBeNull();
        duration!.Value.TotalSeconds.Should().BeGreaterThan(9);
    }

    [Fact]
    public void IncrementIteration_IncrementsByOne()
    {
        var node = new NodeState();
        node.Iteration.Should().Be(1);

        node.IncrementIteration();

        node.Iteration.Should().Be(2);
    }

    [Fact]
    public void GetMetadata_ExistingKey_ReturnsValue()
    {
        var node = new NodeState();
        node.Metadata["score"] = 85;

        node.GetMetadata<int>("score").Should().Be(85);
    }

    [Fact]
    public void GetMetadata_MissingKey_ReturnsDefault()
    {
        var node = new NodeState();

        node.GetMetadata<int>("missing").Should().Be(0);
    }

    [Fact]
    public void GetMetadata_WithDefaultValue_ReturnsDefaultWhenMissing()
    {
        var node = new NodeState();

        node.GetMetadata("missing", 42).Should().Be(42);
    }

    [Fact]
    public void GetMetadata_TypeConversion_ConvertsWhenPossible()
    {
        var node = new NodeState();
        node.Metadata["score"] = 85L; // long, not int

        node.GetMetadata<int>("score").Should().Be(85);
    }

    [Fact]
    public void GetMetadata_IncompatibleType_ReturnsDefault()
    {
        var node = new NodeState();
        node.Metadata["score"] = "not a number";

        node.GetMetadata<int>("score").Should().Be(0);
    }

    [Fact]
    public void SetMetadata_SetsValue()
    {
        var node = new NodeState();

        node.SetMetadata("key", "value");

        node.Metadata["key"].Should().Be("value");
    }

    [Fact]
    public void SetMetadata_OverwritesExisting()
    {
        var node = new NodeState();
        node.SetMetadata("key", "old");

        node.SetMetadata("key", "new");

        node.Metadata["key"].Should().Be("new");
    }
}
