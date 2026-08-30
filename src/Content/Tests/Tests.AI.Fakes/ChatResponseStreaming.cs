using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// Reconstructs a blocking <see cref="ChatResponse"/> as the sequence of
/// <see cref="ChatResponseUpdate"/> frames a real provider would emit for it — one frame per
/// content item, with usage arriving as a trailing <see cref="UsageContent"/> chunk.
/// </summary>
/// <remarks>
/// The single place this logic lives. It was previously copy-pasted between
/// <c>Application.AI.Common.Tests.Fakes.FakeChatClient</c> and
/// <c>Presentation.AgentHub.Tests.Fakes.FakeChatClient</c> — the second copy is what had silently
/// drifted to collapsing every response into one frame, the exact bug Package A of the
/// verification cluster exists to close. Any fake that needs to turn a canned
/// <see cref="ChatResponse"/> into a streaming sequence should call this rather than
/// reimplementing the loop a third time.
/// </remarks>
public static class ChatResponseStreaming
{
    /// <summary>
    /// Converts <paramref name="response"/> into its per-content-item update sequence. Honours
    /// <paramref name="cancellationToken"/> between frames — passed through by a caller's
    /// <c>WithCancellation</c> via <see cref="EnumeratorCancellationAttribute"/> rather than being a
    /// decorative parameter, since the enumeration has no other await point to observe it at.
    /// </summary>
    /// <remarks>
    /// Every frame carries <paramref name="response"/>'s <see cref="ChatResponse.ResponseId"/>,
    /// <see cref="ChatResponse.ConversationId"/>, <see cref="ChatResponse.ModelId"/> and
    /// <see cref="ChatResponse.CreatedAt"/>, and each message's own
    /// <see cref="ChatMessage.MessageId"/> — production coalesces a stream back with
    /// <c>ChatResponseUpdateExtensions.ToChatResponse()</c>, which reads those fields off the
    /// updates, not the original blocking response. Frames with none of them would make a scripted
    /// response look metadata-less on the streaming path while carrying real values on the blocking
    /// one, and would coalesce every message back into one (no <c>MessageId</c> to split on). The
    /// final frame also carries <see cref="ChatResponse.FinishReason"/>, matching where a real
    /// provider's finish reason arrives.
    /// </remarks>
    public static async IAsyncEnumerable<ChatResponseUpdate> ToUpdatesAsync(
        ChatResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var frames = BuildFrames(response);
        for (var i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i == frames.Count - 1) frames[i].FinishReason = response.FinishReason;
            yield return frames[i];
        }

        await Task.CompletedTask;
    }

    private static List<ChatResponseUpdate> BuildFrames(ChatResponse response)
    {
        var frames = new List<ChatResponseUpdate>();

        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
                frames.Add(NewFrame(response, message.Role, message.MessageId, new List<AIContent> { content }));

        if (response.Usage is { } usage)
            frames.Add(NewFrame(response, ChatRole.Assistant, messageId: null, new List<AIContent> { new UsageContent(usage) }));

        return frames;
    }

    private static ChatResponseUpdate NewFrame(ChatResponse response, ChatRole role, string? messageId, IList<AIContent> contents) =>
        new(role, contents)
        {
            ResponseId = response.ResponseId,
            MessageId = messageId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
        };
}
