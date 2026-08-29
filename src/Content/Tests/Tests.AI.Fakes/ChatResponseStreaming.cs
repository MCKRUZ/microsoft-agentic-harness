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
    public static async IAsyncEnumerable<ChatResponseUpdate> ToUpdatesAsync(
        ChatResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(message.Role, new List<AIContent> { content });
            }

        if (response.Usage is { } usage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
        }

        await Task.CompletedTask;
    }
}
