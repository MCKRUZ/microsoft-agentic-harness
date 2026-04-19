using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="NodeState"/> logic methods: GetMetadata, SetMetadata,
/// IsComplete, IsActive, HasFailed, GetDuration, IncrementIteration.
/// </summary>
public class NodeStateLogicTests
{
    // ── GetMetadata ──

    [Fact]
    public void GetMetadata_ExistingKey_ReturnsTypedValue()
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
    public void GetMetadata_MissingKey_ReturnsCustomDefault()
    {
        var node = new NodeState();

        node.GetMetadata("missing", defaultValue: -1).Should().Be(-1);
    }

    [Fact]
    public void GetMetadata_TypeMismatch_AttemptsConversion()
    {
        var node = new NodeState();
        node.Metadata["count"] = 42;

        node.GetMetadata<string>("count").Should().Be("42");
    }

    [Fact]
    public void GetMetadata_InconvertibleType_ReturnsDefault()
    {
        var node = new NodeState();
        node.Metadata["data"] = "not-a-guid";

        node.GetMetadata<Guid>("data").Should().Be(Guid.Empty);
    }

    // ── SetMetadata ──

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
        node.Metadata["key"] = "old";

        node.SetMetadata("key", "new");

        node.Metadata["key"].Should().Be("new");
    }

    // ── IsComplete ──

    [Fact]
    public void IsComplete_CompletedStatus_ReturnsTrue()
    {
        var node = new NodeState { Status = "completed" };

        node.IsComplete().Should().BeTrue();
    }

    [Fact]
    public void IsComplete_InProgressStatus_ReturnsFalse()
    {
        var node = new NodeState { Status = "in_progress" };

        node.IsComplete().Should().BeFalse();
    }

    // ── IsActive ──

    [Theory]
    [InlineData("in_progress")]
    [InlineData("awaiting_input")]
    [InlineData("awaiting_approval")]
    public void IsActive_ActiveStatuses_ReturnsTrue(string status)
    {
        var node = new NodeState { Status = status };

        node.IsActive().Should().BeTrue();
    }

    [Theory]
    [InlineData("not_started")]
    [InlineData("completed")]
    [InlineData("failed")]
    public void IsActive_InactiveStatuses_ReturnsFalse(string status)
    {
        var node = new NodeState { Status = status };

        node.IsActive().Should().BeFalse();
    }

    // ── HasFailed ──

    [Fact]
    public void HasFailed_FailedStatus_ReturnsTrue()
    {
        var node = new NodeState { Status = "failed" };

        node.HasFailed().Should().BeTrue();
    }

    [Fact]
    public void HasFailed_CompletedStatus_ReturnsFalse()
    {
        var node = new NodeState { Status = "completed" };

        node.HasFailed().Should().BeFalse();
    }

    // ── GetDuration ──

    [Fact]
    public void GetDuration_NotStarted_ReturnsNull()
    {
        var node = new NodeState();

        node.GetDuration().Should().BeNull();
    }

    [Fact]
    public void GetDuration_StartedAndCompleted_ReturnsDuration()
    {
        var start = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 1, 1, 10, 30, 0, DateTimeKind.Utc);
        var node = new NodeState { StartedAt = start, CompletedAt = end };

        var duration = node.GetDuration();

        duration.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void GetDuration_StartedNotCompleted_ReturnsElapsedToNow()
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        var node = new NodeState { StartedAt = start };

        var duration = node.GetDuration();

        duration.Should().NotBeNull();
        duration!.Value.TotalMinutes.Should().BeApproximately(5, 1);
    }

    // ── IncrementIteration ──

    [Fact]
    public void IncrementIteration_IncrementsFromDefault()
    {
        var node = new NodeState(); // default Iteration = 1

        node.IncrementIteration();

        node.Iteration.Should().Be(2);
    }

    [Fact]
    public void IncrementIteration_MultipleIncrements()
    {
        var node = new NodeState();

        node.IncrementIteration();
        node.IncrementIteration();
        node.IncrementIteration();

        node.Iteration.Should().Be(4);
    }
}
