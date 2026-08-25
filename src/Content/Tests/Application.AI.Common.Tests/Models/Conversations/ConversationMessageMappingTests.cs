using System.Text.Json;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Proves <see cref="ConversationMessageMapping.ToChatMessages"/> expands a persisted
/// <see cref="ConversationMessage.ToolCalls"/> record back into the real
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pair the model actually
/// produced (#249 item 6), rather than the narrated-text-only projection this mapping used to be
/// pinned to. The double-recording defect that text-only projection was guarding against
/// (<c>ToolDiagnosticsMiddleware</c> re-recording replayed tool results as live activity) is now
/// fixed structurally by <c>ReplayedToolCallScope</c> — see
/// <c>ToolDiagnosticsMiddlewareTests.InvokeNext_ResultCallIdInReplayedScope_DoesNotCallAppendTrace</c>
/// — so this file's job changed from "prevent expansion" to "prove expansion is correct."
/// </summary>
public sealed class ConversationMessageMappingTests
{
    [Fact]
    public void ToChatMessages_AssistantRowWithSingleToolCall_ExpandsToCallResultThenText()
    {
        var toolCall = new ToolCallRecord(
            "search",
            """{"query":"weather"}""",
            """{"result":"sunny"}""",
            DurationMs: 42,
            CallId: "call-1",
            RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.User, "what's the weather?", DateTimeOffset.UtcNow),
            new(
                Guid.NewGuid(), MessageRole.Assistant, "it's sunny", DateTimeOffset.UtcNow,
                ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.Should().HaveCount(4);
        replayed[0].Role.Should().Be(ChatRole.User);

        replayed[1].Role.Should().Be(ChatRole.Assistant);
        var call = replayed[1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionCallContent>().Subject;
        call.CallId.Should().Be("call-1");
        call.Name.Should().Be("search");
        ((JsonElement)call.Arguments!["query"]!).GetString().Should().Be("weather");

        replayed[2].Role.Should().Be(ChatRole.Tool);
        var toolResult = replayed[2].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>().Subject;
        toolResult.CallId.Should().Be("call-1");
        toolResult.Result.Should().Be("""{"result":"sunny"}""");

        replayed[3].Role.Should().Be(ChatRole.Assistant);
        replayed[3].Text.Should().Be("it's sunny");
    }

    [Fact]
    public void ToChatMessages_MultipleRounds_ExpandsInRoundOrdinalOrder()
    {
        var second = new ToolCallRecord("weather", """{"city":"NYC"}""", """{"temp":72}""", 10, "call-2", RoundOrdinal: 1);
        var first = new ToolCallRecord("search", """{"query":"nyc weather"}""", """{"hit":true}""", 20, "call-1", RoundOrdinal: 0);

        // Deliberately out of order in the persisted list — expansion must sort by RoundOrdinal, not list order.
        var transcript = new List<ConversationMessage>
        {
            new(
                Guid.NewGuid(), MessageRole.Assistant, "it's 72 in NYC", DateTimeOffset.UtcNow,
                ToolCalls: [second, first]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.Should().HaveCount(5);
        ((FunctionCallContent)replayed[0].Contents.Single()).CallId.Should().Be("call-1");
        ((FunctionResultContent)replayed[1].Contents.Single()).CallId.Should().Be("call-1");
        ((FunctionCallContent)replayed[2].Contents.Single()).CallId.Should().Be("call-2");
        ((FunctionResultContent)replayed[3].Contents.Single()).CallId.Should().Be("call-2");
        replayed[4].Text.Should().Be("it's 72 in NYC");
    }

    [Fact]
    public void ToChatMessages_ToolOnlyTurnWithNoFinalText_DoesNotAppendEmptyTrailingMessage()
    {
        var toolCall = new ToolCallRecord("search", null, """{"result":"ok"}""", 5, "call-1", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.Should().HaveCount(2, "an empty final Content must not produce a trailing empty assistant message");
    }

    [Fact]
    public void ToChatMessages_NoArguments_ReconstructsNullArguments()
    {
        var toolCall = new ToolCallRecord("ping", null, "pong", 1, "call-1", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        ((FunctionCallContent)replayed[0].Contents.Single()).Arguments.Should().BeNull();
    }

    [Fact]
    public void ToChatMessages_InputNotValidJson_ReconstructsRawFallback()
    {
        var toolCall = new ToolCallRecord(
            "search", "[withheld: too large to replay safely]", "ok", 1, "call-1", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        var call = (FunctionCallContent)replayed[0].Contents.Single();
        call.Arguments.Should().ContainKey("_raw").WhoseValue.Should()
            .Be("[withheld: too large to replay safely]");
    }

    [Fact]
    public void ToChatMessages_NullOutput_ReconstructsNullResult()
    {
        var toolCall = new ToolCallRecord("search", null, null, 1, "call-1", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        ((FunctionResultContent)replayed[1].Contents.Single()).Result.Should().BeNull();
    }

    [Fact]
    public void ToChatMessages_NullCallId_SynthesizesUniqueIdSharedBetweenCallAndResult()
    {
        var toolCall = new ToolCallRecord("search", null, "ok", 1, CallId: null, RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        var callId = ((FunctionCallContent)replayed[0].Contents.Single()).CallId;
        callId.Should().NotBeNullOrEmpty();
        ((FunctionResultContent)replayed[1].Contents.Single()).CallId.Should().Be(callId);
    }

    [Fact]
    public void ToChatMessages_SameCallIdPersistedOnTwoTurns_ReplaysWithDistinctIds()
    {
        // Security-gate finding: some provider connectors number call ids per-turn and reset them
        // (call_0, call_1, then call_0 again next turn — ToolCallOrderingSink documents this as real).
        // Two turns can therefore each persist "call_0". Replaying both with that shared id puts two
        // tool_calls entries carrying one id into a single request, which providers reject — and since
        // the window is rebuilt from persisted rows every turn, that rejection would recur forever.
        var turnOneCall = new ToolCallRecord("search", null, "first", 1, "call_0", RoundOrdinal: 0);
        var turnTwoCall = new ToolCallRecord("search", null, "second", 1, "call_0", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "turn one", DateTimeOffset.UtcNow, ToolCalls: [turnOneCall]),
            new(Guid.NewGuid(), MessageRole.Assistant, "turn two", DateTimeOffset.UtcNow, ToolCalls: [turnTwoCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        var callIds = replayed.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => c.CallId).ToList();
        callIds.Should().HaveCount(2);
        callIds.Should().OnlyHaveUniqueItems(
            "two tool calls in one replayed window must never share an id, whatever was persisted");

        // Each result must still pair with its own call, or the conversation is malformed a different way.
        var resultIds = replayed.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.CallId).ToList();
        resultIds.Should().BeEquivalentTo(callIds,
            "the synthesized id must be applied to the call AND its matching result, keeping them paired");
    }

    [Fact]
    public void ToChatMessages_DistinctCallIdsAcrossTurns_ArePreservedVerbatim()
    {
        // Control for the test above: without it, that assertion would pass just as well against an
        // implementation that synthesized a fresh id for every call and discarded the real ones.
        var turnOneCall = new ToolCallRecord("search", null, "first", 1, "call_a", RoundOrdinal: 0);
        var turnTwoCall = new ToolCallRecord("search", null, "second", 1, "call_b", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "turn one", DateTimeOffset.UtcNow, ToolCalls: [turnOneCall]),
            new(Guid.NewGuid(), MessageRole.Assistant, "turn two", DateTimeOffset.UtcNow, ToolCalls: [turnTwoCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => c.CallId).Should().Equal("call_a", "call_b");
    }

    [Fact]
    public void ToChatMessages_ReplayDisabled_SkipsToolCallExpansionAndKeepsTextOnly()
    {
        // The operator kill switch, at the mapping layer: an already-persisted tool call must not
        // replay when the deployment has turned the feature off.
        var toolCall = new ToolCallRecord("search", null, "sunny", 1, "call-1", RoundOrdinal: 0);

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "it's sunny", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: false);

        replayed.Should().ContainSingle();
        replayed[0].Text.Should().Be("it's sunny");
        replayed.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should().BeEmpty();
    }

    [Fact]
    public void ToChatMessages_NonAssistantRowOrNoToolCalls_ProjectsTextOnlyAsBefore()
    {
        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.User, "hi", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), MessageRole.Assistant, "hello", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), MessageRole.System, "be nice", DateTimeOffset.UtcNow),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript);

        replayed.Should().HaveCount(3);
        replayed.SelectMany(m => m.Contents).Should().AllBeOfType<TextContent>();
    }
}
