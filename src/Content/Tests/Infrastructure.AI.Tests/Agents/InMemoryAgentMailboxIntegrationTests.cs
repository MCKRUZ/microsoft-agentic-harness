using Domain.AI.Agents;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Integration tests for <see cref="InMemoryAgentMailbox"/> covering send/receive,
/// WaitForResponseAsync with timeout and correlation matching, and re-queuing logic.
/// </summary>
public sealed class InMemoryAgentMailboxIntegrationTests
{
    private readonly InMemoryAgentMailbox _sut = new();

    private static AgentMessage CreateMessage(
        string toAgentId,
        string? correlationId = null,
        string content = "test") => new()
    {
        ToAgentId = toAgentId,
        FromAgentId = "sender",
        CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
        Content = content,
        MessageType = AgentMessageType.Task,
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SendAsync_ReceiveAsync_RoundTrips()
    {
        var message = CreateMessage("agent-1");

        await _sut.SendAsync(message);
        var received = await _sut.ReceiveAsync("agent-1");

        received.Should().ContainSingle()
            .Which.CorrelationId.Should().Be(message.CorrelationId);
    }

    [Fact]
    public async Task ReceiveAsync_EmptyMailbox_ReturnsEmpty()
    {
        var received = await _sut.ReceiveAsync("empty-agent");

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_DrainsAllMessages()
    {
        await _sut.SendAsync(CreateMessage("agent-2", content: "msg1"));
        await _sut.SendAsync(CreateMessage("agent-2", content: "msg2"));
        await _sut.SendAsync(CreateMessage("agent-2", content: "msg3"));

        var first = await _sut.ReceiveAsync("agent-2");
        first.Should().HaveCount(3);

        var second = await _sut.ReceiveAsync("agent-2");
        second.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_DifferentAgents_Isolated()
    {
        await _sut.SendAsync(CreateMessage("agent-a"));
        await _sut.SendAsync(CreateMessage("agent-b"));

        var a = await _sut.ReceiveAsync("agent-a");
        var b = await _sut.ReceiveAsync("agent-b");

        a.Should().ContainSingle();
        b.Should().ContainSingle();
    }

    [Fact]
    public async Task WaitForResponseAsync_MatchingCorrelation_ReturnsMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        var message = CreateMessage("agent-wait", correlationId, "response");

        await _sut.SendAsync(message);

        var result = await _sut.WaitForResponseAsync(
            "agent-wait", correlationId, TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        result!.CorrelationId.Should().Be(correlationId);
    }

    [Fact]
    public async Task WaitForResponseAsync_Timeout_ReturnsNull()
    {
        var result = await _sut.WaitForResponseAsync(
            "agent-timeout", "no-match", TimeSpan.FromMilliseconds(50));

        result.Should().BeNull();
    }

    [Fact]
    public async Task WaitForResponseAsync_NonMatchingMessages_ReQueued()
    {
        var targetCorrelation = Guid.NewGuid().ToString();
        var otherMessage = CreateMessage("agent-requeue", "other-correlation", "other");

        await _sut.SendAsync(otherMessage);
        await _sut.SendAsync(CreateMessage("agent-requeue", targetCorrelation, "target"));

        var result = await _sut.WaitForResponseAsync(
            "agent-requeue", targetCorrelation, TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        result!.Content.Should().Be("target");

        var remaining = await _sut.ReceiveAsync("agent-requeue");
        remaining.Should().ContainSingle()
            .Which.Content.Should().Be("other");
    }

    [Fact]
    public async Task WaitForResponseAsync_TimeoutReQueuesAllNonMatching()
    {
        var msg1 = CreateMessage("agent-timeout-requeue", "corr-1", "first");
        var msg2 = CreateMessage("agent-timeout-requeue", "corr-2", "second");
        await _sut.SendAsync(msg1);
        await _sut.SendAsync(msg2);

        var result = await _sut.WaitForResponseAsync(
            "agent-timeout-requeue", "nonexistent", TimeSpan.FromMilliseconds(100));

        result.Should().BeNull();

        var remaining = await _sut.ReceiveAsync("agent-timeout-requeue");
        remaining.Should().HaveCount(2);
    }

    [Fact]
    public async Task WaitForResponseAsync_DelayedMessage_ReceivesWhenArrives()
    {
        var correlationId = Guid.NewGuid().ToString();

        var waitTask = _sut.WaitForResponseAsync(
            "agent-delayed", correlationId, TimeSpan.FromSeconds(5));

        await Task.Delay(50);
        await _sut.SendAsync(CreateMessage("agent-delayed", correlationId, "delayed-response"));

        var result = await waitTask;

        result.Should().NotBeNull();
        result!.Content.Should().Be("delayed-response");
    }

    [Fact]
    public async Task SendAsync_NullMessage_Throws()
    {
        var act = () => _sut.SendAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReceiveAsync_NullAgentId_Throws()
    {
        var act = () => _sut.ReceiveAsync(null!);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
