using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Microsoft.Extensions.Logging;

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
/// This is the one place that reads the window for a dispatch, so the transports that need it cannot
/// disagree about which messages are prior.
/// </para>
/// </remarks>
public static class ConversationDispatchHistory
{
    /// <summary>
    /// Returns up to <paramref name="maxPriorMessages"/> messages that precede the message with id
    /// <paramref name="inFlightMessageId"/>, which the caller has just stored and is about to dispatch.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="conversationId">The conversation being dispatched on.</param>
    /// <param name="callerId">The authenticated caller; the store refuses another owner's conversation.</param>
    /// <param name="maxPriorMessages">
    /// How many <em>prior</em> messages to return. Zero or negative returns none, as the store defines it.
    /// </param>
    /// <param name="inFlightMessageId">The id the message being sent was stored under.</param>
    /// <param name="logger">Where a window that does not end with that message is reported.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The prior messages, oldest first; empty when the conversation has none or does not exist.</returns>
    /// <remarks>
    /// <para>
    /// Matched by <em>id</em>, not by content: the id is what the caller stored the message under, so the
    /// match survives a store that normalises text on write, where a content comparison would quietly
    /// stop excluding and bring the duplicate back.
    /// </para>
    /// <para>
    /// The window is requested one message larger than asked for, so excluding the in-flight message
    /// costs the model none of its context. If the window does not end with that message nothing is
    /// removed — dropping the last message blindly would discard real history — and a warning says so,
    /// because it means a transport stopped storing the message before reading the window.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<ConversationMessage>> ReadPriorToAsync(
        IConversationStore store,
        string conversationId,
        string callerId,
        int maxPriorMessages,
        Guid inFlightMessageId,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        var window = await store.GetHistoryForDispatch(
            conversationId, callerId, WindowIncludingInFlightMessage(maxPriorMessages), ct) ?? [];

        var end = window.Count;
        if (end > 0 && window[end - 1].Id == inFlightMessageId)
        {
            end--;
        }
        else if (end > 0)
        {
            logger.LogWarning(
                "The history window for conversation {ConversationId} does not end with the message being "
                    + "sent ({MessageId}); it is dispatched as read, so that message may reach the model twice.",
                conversationId, inFlightMessageId);
        }

        // Never more than asked for, whichever way the window came back.
        var start = Math.Max(0, end - Math.Max(0, maxPriorMessages));

        return start == 0 && end == window.Count ? window : window.Skip(start).Take(end - start).ToList();
    }

    // Saturating: int.MaxValue is a plausible "no limit", and one more than that wraps to a negative
    // window, which the store reads as none — every turn would silently lose all its history.
    private static int WindowIncludingInFlightMessage(int maxPriorMessages) =>
        maxPriorMessages <= 0 ? 0 : maxPriorMessages == int.MaxValue ? int.MaxValue : maxPriorMessages + 1;
}
