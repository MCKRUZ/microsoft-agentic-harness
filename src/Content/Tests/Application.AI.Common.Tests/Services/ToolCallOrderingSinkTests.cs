using Application.AI.Common.Interfaces;
using Application.AI.Common.Services;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ToolCallOrderingSink"/> — the per-turn decorator enforcing that a
/// TOOL_CALL_RESULT never streams without a preceding TOOL_CALL_START, and a duplicate start for an
/// already-started id is dropped.
/// </summary>
public class ToolCallOrderingSinkTests
{
    private sealed class RecordingSink : IAgentTurnStreamSink
    {
        public List<string> Calls { get; } = new();

        public Task EmitAsync(string delta, CancellationToken cancellationToken)
        {
            Calls.Add($"delta:{delta}");
            return Task.CompletedTask;
        }

        public Task EmitToolCallAsync(string toolCallId, string toolCallName, StreamedToolCallArguments args, CancellationToken cancellationToken)
        {
            Calls.Add($"start:{toolCallId}");
            return Task.CompletedTask;
        }

        public Task EmitToolCallResultAsync(string toolCallId, string result, CancellationToken cancellationToken)
        {
            Calls.Add($"result:{toolCallId}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DuplicateCallId_SecondStartIsDropped()
    {
        var inner = new RecordingSink();
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sut.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);

        inner.Calls.Should().Equal("start:call-1");
    }

    [Fact]
    public async Task ResultWithoutStart_IsDropped()
    {
        var inner = new RecordingSink();
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitToolCallResultAsync("call-1", "42", CancellationToken.None);

        inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ResultAfterStart_IsForwarded()
    {
        var inner = new RecordingSink();
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sut.EmitToolCallResultAsync("call-1", "42", CancellationToken.None);

        inner.Calls.Should().Equal("start:call-1", "result:call-1");
    }

    [Fact]
    public async Task TwoDistinctCallIds_BothForwarded()
    {
        var inner = new RecordingSink();
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sut.EmitToolCallAsync("call-2", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sut.EmitToolCallResultAsync("call-1", "a", CancellationToken.None);
        await sut.EmitToolCallResultAsync("call-2", "b", CancellationToken.None);

        inner.Calls.Should().Equal("start:call-1", "start:call-2", "result:call-1", "result:call-2");
    }

    /// <summary>
    /// A sink whose onToolCall callback is a no-op (only onToolCallResult is wired) must still
    /// register the call id for later correlation — proves the decorator records BEFORE delegating,
    /// not only when the inner sink actually does something with the start.
    /// </summary>
    [Fact]
    public async Task StartWithInnerSinkThatNoOpsOnStart_StillRegistersForLaterResult()
    {
        var inner = new AgentTurnStreamSink(
            onDelta: (_, _) => Task.CompletedTask,
            onToolCall: null,
            onToolCallResult: (id, result, _) => { CapturedResult = (id, result); return Task.CompletedTask; });
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sut.EmitToolCallResultAsync("call-1", "42", CancellationToken.None);

        CapturedResult.Should().Be(("call-1", "42"));
    }

    private (string Id, string Result)? CapturedResult { get; set; }

    /// <summary>
    /// Two separate instances (one per turn) must not share state — pins the per-turn scoping that
    /// makes this decorator safe for a multi-turn bundle run, where a provider may reuse call ids
    /// across turns.
    /// </summary>
    [Fact]
    public async Task SeparateInstances_DoNotShareState()
    {
        var innerA = new RecordingSink();
        var innerB = new RecordingSink();
        var turnA = new ToolCallOrderingSink(innerA);
        var turnB = new ToolCallOrderingSink(innerB);

        await turnA.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        // A fresh instance for the next turn must not treat "call-1" as already started.
        await turnB.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);

        innerA.Calls.Should().Equal("start:call-1");
        innerB.Calls.Should().Equal("start:call-1");
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        var act = () => new ToolCallOrderingSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EmitAsync_PassesThroughUnchanged()
    {
        var inner = new RecordingSink();
        var sut = new ToolCallOrderingSink(inner);

        await sut.EmitAsync("hello", CancellationToken.None);

        inner.Calls.Should().Equal("delta:hello");
    }
}
