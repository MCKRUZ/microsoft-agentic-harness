using Application.AI.Common.Services;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Unit tests for <see cref="AgentTurnStreamSink"/> — the ambient, delegate-backed sink
/// the orchestrator attaches so the agent-turn handler streams token deltas to the transport.
/// </summary>
public class AgentTurnStreamSinkTests
{
    [Fact]
    public async Task EmitAsync_ForwardsDelta_ToTheCallback()
    {
        var received = new List<string>();
        var sink = new AgentTurnStreamSink((delta, _) => { received.Add(delta); return Task.CompletedTask; });

        await sink.EmitAsync("Hello ", CancellationToken.None);
        await sink.EmitAsync("world", CancellationToken.None);

        received.Should().Equal("Hello ", "world");
    }

    [Fact]
    public async Task EmitAsync_IgnoresEmptyDelta_WithoutInvokingTheCallback()
    {
        var invoked = false;
        var sink = new AgentTurnStreamSink((_, _) => { invoked = true; return Task.CompletedTask; });

        await sink.EmitAsync("", CancellationToken.None);

        invoked.Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullCallback_Throws()
    {
        var act = () => new AgentTurnStreamSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Current_IsNullByDefault_AndRoundTrips()
    {
        AgentTurnStreamSink.Current.Should().BeNull();

        var sink = new AgentTurnStreamSink((_, _) => Task.CompletedTask);
        AgentTurnStreamSink.Current = sink;
        try
        {
            AgentTurnStreamSink.Current.Should().BeSameAs(sink);
        }
        finally
        {
            AgentTurnStreamSink.Current = null;
        }
    }

    [Fact]
    public async Task EmitToolCallAsync_ForwardsToTheCallback()
    {
        (string Id, string Name, string Args)? received = null;
        var sink = new AgentTurnStreamSink(
            (_, _) => Task.CompletedTask,
            onToolCall: (id, name, args, _) => { received = (id, name, args); return Task.CompletedTask; });

        await sink.EmitToolCallAsync("call-1", "search", "{\"q\":\"x\"}", CancellationToken.None);

        received.Should().Be(("call-1", "search", "{\"q\":\"x\"}"));
    }

    [Fact]
    public async Task EmitToolCallAsync_NoCallbackConfigured_NoOps()
    {
        var sink = new AgentTurnStreamSink((_, _) => Task.CompletedTask);

        var act = () => sink.EmitToolCallAsync("call-1", "search", "{}", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EmitToolCallResultAsync_ForwardsToTheCallback()
    {
        (string Id, string Result)? received = null;
        var sink = new AgentTurnStreamSink(
            (_, _) => Task.CompletedTask,
            onToolCallResult: (id, result, _) => { received = (id, result); return Task.CompletedTask; });

        await sink.EmitToolCallResultAsync("call-1", "42", CancellationToken.None);

        received.Should().Be(("call-1", "42"));
    }

    [Fact]
    public async Task EmitToolCallResultAsync_NoCallbackConfigured_NoOps()
    {
        var sink = new AgentTurnStreamSink((_, _) => Task.CompletedTask);

        var act = () => sink.EmitToolCallResultAsync("call-1", "42", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
