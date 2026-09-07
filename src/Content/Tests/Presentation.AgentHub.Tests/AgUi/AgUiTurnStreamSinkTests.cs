using Application.AI.Common.Interfaces;
using FluentAssertions;
using Moq;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Tests for <see cref="AgUiTurnStreamSink"/> — the translation from
/// <see cref="IAgentTurnStreamSink"/> callbacks (real text deltas, tool calls, tool results) into
/// AG-UI SSE events, and the lazy-start contract <see cref="AgUiRunHandler"/> depends on to avoid
/// emitting an orphaned message frame for a turn that never generates anything.
/// </summary>
public sealed class AgUiTurnStreamSinkTests
{
    private const string MessageId = "msg-1";

    private static (AgUiTurnStreamSink Sink, List<AgUiEvent> Written) Create()
    {
        var written = new List<AgUiEvent>();
        var writer = new Mock<IAgUiEventWriter>();
        writer.Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgUiEvent, CancellationToken>((evt, _) => written.Add(evt))
            .Returns(Task.CompletedTask);
        return (new AgUiTurnStreamSink(writer.Object, MessageId), written);
    }

    [Fact]
    public void NoDeltaEmitted_StartedIsFalse()
    {
        var (sink, written) = Create();

        sink.Started.Should().BeFalse();
        written.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstDelta_EmitsStartThenContent_InThatOrder()
    {
        var (sink, written) = Create();

        await sink.EmitAsync("Hello", CancellationToken.None);

        sink.Started.Should().BeTrue();
        written.Should().HaveCount(2);
        written[0].Should().BeOfType<TextMessageStartEvent>()
            .Which.Should().BeEquivalentTo(new TextMessageStartEvent(MessageId, "assistant"));
        written[1].Should().BeOfType<TextMessageContentEvent>()
            .Which.Should().BeEquivalentTo(new TextMessageContentEvent(MessageId, "Hello"));
    }

    [Fact]
    public async Task SecondDelta_DoesNotRepeatStart()
    {
        var (sink, written) = Create();

        await sink.EmitAsync("Hello", CancellationToken.None);
        await sink.EmitAsync(" there", CancellationToken.None);

        written.OfType<TextMessageStartEvent>().Should().HaveCount(1,
            "TEXT_MESSAGE_START must appear exactly once per message, no matter how many deltas follow");
        written.OfType<TextMessageContentEvent>().Select(e => e.Delta)
            .Should().Equal("Hello", " there");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BlankDelta_ContributesNothing(string? delta)
    {
        var (sink, written) = Create();

        await sink.EmitAsync(delta!, CancellationToken.None);

        sink.Started.Should().BeFalse("an empty delta must not itself start the message");
        written.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCall_EmitsStartArgsEnd_InOrder_WithoutTouchingTextFraming()
    {
        var (sink, written) = Create();

        await sink.EmitToolCallAsync(
            "call-1", "search_memory", new StreamedToolCallArguments("{\"q\":\"green eyes\"}", false),
            CancellationToken.None);

        sink.Started.Should().BeFalse("a tool call alone must not start the text message frame");
        written.Should().HaveCount(3);
        written[0].Should().BeEquivalentTo(new ToolCallStartEvent("call-1", "search_memory"));
        written[1].Should().BeEquivalentTo(new ToolCallArgsEvent("call-1", "{\"q\":\"green eyes\"}", null));
        written[2].Should().BeEquivalentTo(new ToolCallEndEvent("call-1"));
    }

    [Fact]
    public async Task ToolCall_WithheldArguments_ArgsEventCarriesWithheldTrue()
    {
        var (sink, written) = Create();

        await sink.EmitToolCallAsync(
            "call-1", "big_tool", new StreamedToolCallArguments("{}", true), CancellationToken.None);

        written.OfType<ToolCallArgsEvent>().Single().Withheld.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallResult_EmitsToolCallResultEvent()
    {
        var (sink, written) = Create();

        await sink.EmitToolCallResultAsync(
            "call-1", new StreamedToolCallResult("42 memories found", false), CancellationToken.None);

        written.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ToolCallResultEvent("call-1", "42 memories found", null));
    }

    [Fact]
    public async Task ToolCallResult_Withheld_CarriesEmptyTextAndWithheldTrue()
    {
        var (sink, written) = Create();

        await sink.EmitToolCallResultAsync(
            "call-1", new StreamedToolCallResult("", true), CancellationToken.None);

        var evt = written.OfType<ToolCallResultEvent>().Single();
        evt.Result.Should().BeEmpty();
        evt.Withheld.Should().BeTrue();
    }

    [Fact]
    public async Task TextAndToolCallsInterleave_InCallOrder()
    {
        // A realistic turn: some text, then a tool call and its result, then more text.
        var (sink, written) = Create();

        await sink.EmitAsync("Let me check. ", CancellationToken.None);
        await sink.EmitToolCallAsync(
            "call-1", "search_memory", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await sink.EmitToolCallResultAsync(
            "call-1", new StreamedToolCallResult("found it", false), CancellationToken.None);
        await sink.EmitAsync("Found it.", CancellationToken.None);

        written.Select(e => e.GetType().Name).Should().Equal(
            nameof(TextMessageStartEvent),
            nameof(TextMessageContentEvent),
            nameof(ToolCallStartEvent),
            nameof(ToolCallArgsEvent),
            nameof(ToolCallEndEvent),
            nameof(ToolCallResultEvent),
            nameof(TextMessageContentEvent));
    }
}
