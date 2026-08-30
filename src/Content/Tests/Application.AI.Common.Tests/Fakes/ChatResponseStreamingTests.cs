using FluentAssertions;
using Microsoft.Extensions.AI;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// Proves <see cref="ChatResponseStreaming.ToUpdatesAsync"/> carries response-level metadata onto
/// every emitted frame (M5 of Package A's review) — without it, a scripted response with a real
/// <c>ResponseId</c>/<c>ConversationId</c>/<c>ModelId</c>/<c>MessageId</c>/<c>FinishReason</c> would
/// look metadata-less on the streaming path while carrying those values on the blocking path,
/// disagreeing with production's <c>ChatResponseUpdateExtensions.ToChatResponse()</c> coalescing,
/// which reads those fields off the updates rather than the original response.
/// </summary>
public sealed class ChatResponseStreamingTests
{
    private static ChatResponse BuildResponse(params ChatMessage[] messages)
    {
        var response = new ChatResponse(messages)
        {
            ResponseId = "resp-1",
            ConversationId = "conv-1",
            ModelId = "model-x",
            CreatedAt = DateTimeOffset.UnixEpoch,
            FinishReason = ChatFinishReason.Stop,
        };
        return response;
    }

    [Fact]
    public async Task ToUpdatesAsync_CopiesResponseLevelMetadataOntoEveryFrame()
    {
        var message = new ChatMessage(ChatRole.Assistant, "hello") { MessageId = "msg-1" };
        var response = BuildResponse(message);

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in ChatResponseStreaming.ToUpdatesAsync(response))
            frames.Add(frame);

        frames.Should().NotBeEmpty();
        frames.Should().OnlyContain(f =>
            f.ResponseId == "resp-1" && f.ConversationId == "conv-1" && f.ModelId == "model-x"
            && f.CreatedAt == DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task ToUpdatesAsync_PropagatesEachMessagesOwnMessageId()
    {
        var first = new ChatMessage(ChatRole.Assistant, "part one") { MessageId = "msg-1" };
        var second = new ChatMessage(ChatRole.Assistant, "part two") { MessageId = "msg-2" };
        var response = BuildResponse(first, second);

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in ChatResponseStreaming.ToUpdatesAsync(response))
            frames.Add(frame);

        frames.Should().HaveCount(2);
        frames[0].MessageId.Should().Be("msg-1");
        frames[1].MessageId.Should().Be("msg-2");
    }

    [Fact]
    public async Task ToUpdatesAsync_SetsFinishReasonOnlyOnTheLastFrame()
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("a"), new TextContent("b") })
        {
            MessageId = "msg-1",
        };
        var response = BuildResponse(message);

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in ChatResponseStreaming.ToUpdatesAsync(response))
            frames.Add(frame);

        frames.Should().HaveCount(2);
        frames[0].FinishReason.Should().BeNull();
        frames[^1].FinishReason.Should().Be(ChatFinishReason.Stop);
    }
}
