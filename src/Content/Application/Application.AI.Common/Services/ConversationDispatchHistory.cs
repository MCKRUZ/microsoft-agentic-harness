using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services;

/// <summary>
/// Reads the history a turn is dispatched with — the messages <em>before</em> the one being sent.
/// </summary>
/// <remarks>
/// <para>
/// The turn handler contract is "prior history plus the new message", and the handler adds that message itself, so
/// the history must not already hold it or the model sees it twice (#785). The interactive transports store the
/// message only once the turn is admitted (#964), so when the history is read it is not stored yet. An edit or a
/// retry, though, replaces a part of the conversation that is still stored until admission, and its history stops
/// before that part.
/// </para>
/// <para>
/// This is the one place that reads the window for a dispatch, so the transports that need it cannot disagree about
/// which messages are prior.
/// </para>
/// </remarks>
public static class ConversationDispatchHistory
{
    /// <summary>
    /// Returns up to <paramref name="maxPriorMessages"/> messages that precede <paramref name="beforeMessageId"/>, or
    /// the latest ones when it is <see langword="null"/>.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="conversationId">The conversation being dispatched on.</param>
    /// <param name="callerId">The authenticated caller; the store refuses another owner's conversation.</param>
    /// <param name="maxPriorMessages">
    /// How many <em>prior</em> messages to return. Zero or negative returns none, as the store defines it.
    /// </param>
    /// <param name="beforeMessageId">
    /// The first stored message the turn replaces: the message an edit rewrites, or the user message a retry sends
    /// again. <see langword="null"/> for a new message, which replaces nothing.
    /// </param>
    /// <param name="logger">Where a <paramref name="beforeMessageId"/> the conversation does not hold is reported.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The prior messages, oldest first; empty when the conversation has none or does not exist.</returns>
    /// <remarks>
    /// <para>
    /// Matched by <em>id</em>, not by content: the id is what the message was stored under, so the match survives a
    /// store that normalises text on write.
    /// </para>
    /// <para>
    /// An edit or retry can reach far back, so its read takes the whole filtered conversation and cuts it, rather
    /// than a window that might end before the message. If the message is not there the latest messages are returned
    /// and a warning says so: the replacement then applies to nothing, and the turn behaves as a new message.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<ConversationMessage>> ReadPriorAsync(
        IConversationStore store,
        string conversationId,
        string callerId,
        int maxPriorMessages,
        Guid? beforeMessageId,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        if (maxPriorMessages <= 0)
            return [];

        if (beforeMessageId is not { } cut)
            return await store.GetHistoryForDispatch(conversationId, callerId, maxPriorMessages, ct) ?? [];

        var all = await store.GetHistoryForDispatch(conversationId, callerId, int.MaxValue, ct) ?? [];
        var index = IndexOf(all, cut);
        if (index < 0)
        {
            logger.LogWarning(
                "Conversation {ConversationId} does not hold message {MessageId}, which this turn replaces; it is "
                    + "dispatched with the latest history.",
                conversationId, cut);
            index = all.Count;
        }

        return all.Take(index).TakeLast(maxPriorMessages).ToList();
    }

    private static int IndexOf(IReadOnlyList<ConversationMessage> messages, Guid id)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == id)
                return i;
        }

        return -1;
    }
}
