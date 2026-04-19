using Domain.AI.Agents;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Agents;

/// <summary>
/// Tests for <see cref="AgentMessage"/> record — construction, equality, and with-expressions.
/// </summary>
public sealed class AgentMessageTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsAllValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var msg = new AgentMessage
        {
            FromAgentId = "orchestrator",
            ToAgentId = "worker-1",
            Content = "Analyze this file",
            MessageType = AgentMessageType.Task,
            Timestamp = timestamp,
            CorrelationId = "corr-123"
        };

        msg.FromAgentId.Should().Be("orchestrator");
        msg.ToAgentId.Should().Be("worker-1");
        msg.Content.Should().Be("Analyze this file");
        msg.MessageType.Should().Be(AgentMessageType.Task);
        msg.Timestamp.Should().Be(timestamp);
        msg.CorrelationId.Should().Be("corr-123");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var msg1 = CreateMessage(timestamp);
        var msg2 = CreateMessage(timestamp);

        msg1.Should().Be(msg2);
    }

    [Fact]
    public void Equality_DifferentContent_AreNotEqual()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var msg1 = CreateMessage(timestamp) with { Content = "Task A" };
        var msg2 = CreateMessage(timestamp) with { Content = "Task B" };

        msg1.Should().NotBe(msg2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = CreateMessage(DateTimeOffset.UtcNow);
        var updated = original with { MessageType = AgentMessageType.Result };

        updated.MessageType.Should().Be(AgentMessageType.Result);
        original.MessageType.Should().Be(AgentMessageType.Task);
    }

    private static AgentMessage CreateMessage(DateTimeOffset timestamp) =>
        new()
        {
            FromAgentId = "agent-a",
            ToAgentId = "agent-b",
            Content = "payload",
            MessageType = AgentMessageType.Task,
            Timestamp = timestamp,
            CorrelationId = "corr-1"
        };
}
