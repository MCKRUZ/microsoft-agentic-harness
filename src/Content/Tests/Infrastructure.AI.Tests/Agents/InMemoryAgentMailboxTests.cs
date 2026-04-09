using Domain.AI.Agents;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class InMemoryAgentMailboxTests
{
    private readonly InMemoryAgentMailbox _mailbox = new();

    private static AgentMessage CreateMessage(
        string from = "agent-1",
        string to = "agent-2",
        string correlationId = "corr-1",
        AgentMessageType type = AgentMessageType.Task,
        string content = "test payload")
    {
        return new AgentMessage
        {
            FromAgentId = from,
            ToAgentId = to,
            Content = content,
            MessageType = type,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId
        };
    }

    [Fact]
    public async Task Send_ThenReceive_ReturnsMessage()
    {
        var message = CreateMessage();
        await _mailbox.SendAsync(message);

        var received = await _mailbox.ReceiveAsync("agent-2");

        received.Should().HaveCount(1);
        received[0].Content.Should().Be("test payload");
        received[0].FromAgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Receive_EmptyMailbox_ReturnsEmpty()
    {
        var received = await _mailbox.ReceiveAsync("agent-nonexistent");

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitForResponse_MatchingCorrelation_ReturnsMessage()
    {
        var message = CreateMessage(correlationId: "corr-42", type: AgentMessageType.Result);

        // Send before waiting — message is already in channel
        await _mailbox.SendAsync(message);

        var result = await _mailbox.WaitForResponseAsync(
            "agent-2", "corr-42", TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        result!.CorrelationId.Should().Be("corr-42");
        result.MessageType.Should().Be(AgentMessageType.Result);
    }

    [Fact]
    public async Task WaitForResponse_Timeout_ReturnsNull()
    {
        // No messages sent — should timeout
        var result = await _mailbox.WaitForResponseAsync(
            "agent-2", "nonexistent-corr", TimeSpan.FromMilliseconds(50));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentSend_ThreadSafe()
    {
        const int messageCount = 100;
        var tasks = Enumerable.Range(0, messageCount)
            .Select(i => _mailbox.SendAsync(
                CreateMessage(
                    from: $"sender-{i}",
                    to: "receiver",
                    correlationId: $"corr-{i}",
                    content: $"message-{i}")));

        await Task.WhenAll(tasks);

        var received = await _mailbox.ReceiveAsync("receiver");
        received.Should().HaveCount(messageCount);
        received.Select(m => m.Content).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Receive_DrainsChannel_SubsequentCallReturnsEmpty()
    {
        await _mailbox.SendAsync(CreateMessage());
        await _mailbox.SendAsync(CreateMessage(correlationId: "corr-2"));

        var first = await _mailbox.ReceiveAsync("agent-2");
        first.Should().HaveCount(2);

        var second = await _mailbox.ReceiveAsync("agent-2");
        second.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitForResponse_NonMatchingMessages_AreRequeued()
    {
        // Send two messages with different correlation IDs
        await _mailbox.SendAsync(CreateMessage(correlationId: "corr-A", content: "not-this"));
        await _mailbox.SendAsync(CreateMessage(correlationId: "corr-B", content: "want-this"));

        var result = await _mailbox.WaitForResponseAsync(
            "agent-2", "corr-B", TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        result!.Content.Should().Be("want-this");

        // The non-matching message should have been re-queued
        var remaining = await _mailbox.ReceiveAsync("agent-2");
        remaining.Should().HaveCount(1);
        remaining[0].CorrelationId.Should().Be("corr-A");
    }
}
