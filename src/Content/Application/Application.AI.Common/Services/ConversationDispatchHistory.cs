using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;

namespace Application.AI.Common.Services;

/// <summary>
/// Reads the history a turn is dispatched with — the messages <em>before</em> the one being sent.
/// </summary>
/// <remarks>
/// <para>
/// The interactive transports store the user's message before they dispatch the turn, so the window the
/// store returns already ends with it. The turn handler contract is "prior history plus the new
/// message" and adds that message itself, so dispatching the window as it came sent the user's message
/// to the model twice (#785). The bundle path was never affected: it carries history in memory and
/// writes the question and answer together after the turn.
/// </para>
/// <para>
/// This is the one place that reads the window for a dispatch, so the two transports that need it
/// cannot disagree about which messages are prior.
/// </para>
/// </remarks>
public static class ConversationDispatchHistory
{
    /// <summary>
    /// Returns up to <paramref name="maxPriorMessages"/> messages that precede <paramref name="userMessage"/>,
    /// the message about to be dispatched, which is already the last one stored.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="conversationId">The conversation being dispatched on.</param>
    /// <param name="callerId">The authenticated caller; the store refuses another owner's conversation.</param>
    /// <param name="maxPriorMessages">
    /// How many <em>prior</em> messages to return. Zero or negative returns none, as the store defines it.
    /// </param>
    /// <param name="userMessage">The message being sent, as stored.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The prior messages, oldest first; empty when the conversation has none or does not exist.</returns>
    /// <remarks>
    /// The window is requested one message larger than asked for, so excluding the in-flight message
    /// costs the model none of its context. The last message is dropped only when it is a user message
    /// with exactly this content: if the store ever answers a window that does not end with it, nothing
    /// is removed rather than the wrong message.
    /// </remarks>
    public static async Task<IReadOnlyList<ConversationMessage>> ReadPriorToAsync(
        IConversationStore store,
        string conversationId,
        string callerId,
        int maxPriorMessages,
        string userMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var window = await store.GetHistoryForDispatch(
            conversationId, callerId, maxPriorMessages > 0 ? maxPriorMessages + 1 : 0, ct) ?? [];

        IReadOnlyList<ConversationMessage> prior =
            window is [.., { Role: MessageRole.User } last] && string.Equals(last.Content, userMessage, StringComparison.Ordinal)
                ? window.Take(window.Count - 1).ToList()
                : window;

        // Never more than asked for, whichever way the window came back.
        return prior.Count > maxPriorMessages ? prior.TakeLast(Math.Max(0, maxPriorMessages)).ToList() : prior;
    }
}
