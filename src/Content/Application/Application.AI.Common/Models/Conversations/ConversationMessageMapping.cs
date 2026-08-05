using Microsoft.Extensions.AI;

namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// Projects stored transcript messages onto the shape the agent framework dispatches, so every caller
/// that replays a conversation to a model does it the same way.
/// </summary>
/// <remarks>
/// <para>
/// This mapping was written out by hand in three places — the SignalR orchestrator, the AG-UI handler,
/// and the shared multi-turn loop — each an identical <c>switch</c> over the same enum. Three copies of
/// one mapping is three chances to add a role in one of them: a role missing from a copy does not fail,
/// it silently replays as the fallback, and the only symptom is a model that was told the wrong speaker
/// said something.
/// </para>
/// <para>
/// The fallback is deliberate rather than defensive. <see cref="MessageRole"/> is closed and every
/// member is mapped, so the arm is unreachable today; it exists because a role added later must not
/// throw halfway through building a prompt, and <see cref="ChatRole.User"/> is the reading that
/// attributes an unknown speaker to the least privileged one.
/// </para>
/// </remarks>
public static class ConversationMessageMapping
{
    /// <summary>Maps a stored message role onto the agent framework's chat role.</summary>
    /// <param name="role">The stored role.</param>
    /// <remarks>
    /// Private deliberately. Every caller wants a whole window projected, not one role converted, and
    /// in a template consumers clone and extend, a public member is a supported member.
    /// </remarks>
    private static ChatRole ToChatRole(MessageRole role) => role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        MessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User,
    };

    /// <summary>
    /// Projects a transcript window onto chat messages, oldest first, ready to seed a dispatch.
    /// </summary>
    /// <param name="messages">The window, in transcript order.</param>
    /// <remarks>
    /// Widget messages carry empty content and are excluded upstream by
    /// <c>IConversationStore.GetHistoryForDispatch</c>, so this is a straight projection.
    /// </remarks>
    public static IReadOnlyList<ChatMessage> ToChatMessages(IReadOnlyList<ConversationMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages.Select(m => new ChatMessage(ToChatRole(m.Role), m.Content)).ToList();
    }
}
