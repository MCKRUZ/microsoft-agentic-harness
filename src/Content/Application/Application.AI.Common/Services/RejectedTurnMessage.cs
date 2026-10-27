using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.Common.Exceptions.ExceptionTypes;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services;

/// <summary>
/// Handles the caller's stored message when a turn is rejected before the model sees it (#959).
/// </summary>
/// <remarks>
/// <para>
/// The interactive transports store the caller's message before they dispatch the turn, and the next turn reads its
/// history back from the store, where nothing screens it. A turn rejected by request screening
/// (<see cref="IScreeningRefusal"/>) or by request validation (<see cref="ValidationException"/>, which runs before
/// screening) never had its message admitted, so a message left in the store would reach the model as history the
/// moment the caller sent anything that passes.
/// </para>
/// <para>
/// Both transports go through <see cref="RejectAsync"/>, so they cannot disagree about which rejections remove the
/// message or what the caller is told.
/// </para>
/// </remarks>
public static class RejectedTurnMessage
{
    /// <summary>What the caller is told when validation rejects the message.</summary>
    public const string NotAcceptedMessage = "This message could not be accepted. Check its length and try again.";

    /// <summary>Whether <paramref name="ex"/> rejected the turn before the model saw the caller's message.</summary>
    /// <param name="ex">The exception the turn dispatch threw.</param>
    /// <returns><see langword="true"/> for a screening refusal or a validation rejection.</returns>
    public static bool IsRejection(Exception ex) => ex is IScreeningRefusal or ValidationException;

    /// <summary>
    /// Removes the rejected message when it should go, and returns what the caller is told. Never contains the rejected
    /// text.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="conversationId">The conversation the message was stored in.</param>
    /// <param name="callerId">The authenticated caller; the store refuses another owner's conversation.</param>
    /// <param name="messageId">The id the rejected message was stored under.</param>
    /// <param name="storedForThisTurn">
    /// <see langword="true"/> when the transport stored the message for this turn (a send or an edit);
    /// <see langword="false"/> when it re-sends a message an earlier turn stored (a retry).
    /// </param>
    /// <param name="rejection">The rejection, as recognised by <see cref="IsRejection"/>.</param>
    /// <param name="logger">Where the rejection, and a failed removal, are reported.</param>
    /// <returns>The refusal's own message, or <see cref="NotAcceptedMessage"/> for a validation rejection.</returns>
    /// <remarks>
    /// The removal takes no cancellation token on purpose: it is what keeps the message out of the next turn's
    /// history, so a caller disconnecting after the rejection must not cancel it. It is best effort: a failure is
    /// logged as the replay risk it is, and the caller is still told the turn was rejected.
    /// </remarks>
    public static async Task<string> RejectAsync(
        IConversationStore store, string conversationId, string callerId, Guid messageId, bool storedForThisTurn,
        Exception rejection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rejection);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogWarning(rejection, "Turn on conversation {ConversationId} rejected before dispatch.", conversationId);
        if (ShouldRemove(rejection, storedForThisTurn))
        {
            try
            {
                await store.TruncateFromMessageAsync(conversationId, callerId, messageId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to remove the rejected message from conversation {ConversationId}; it will be replayed as history.",
                    conversationId);
            }
        }

        return rejection is IScreeningRefusal refusal ? refusal.ClientMessage : NotAcceptedMessage;
    }

    /// <summary>Whether the rejected message is removed from the store.</summary>
    /// <param name="rejection">The rejection, as recognised by <see cref="IsRejection"/>.</param>
    /// <param name="storedForThisTurn">Whether the transport stored the message for this turn.</param>
    /// <returns>
    /// <see langword="true"/> unless a retried message could not be screened. A message stored for this turn was never
    /// admitted, and a verdict against a retried one means it should not stay either. A screening outage is no verdict,
    /// and removing the retried message would delete history an earlier turn stored.
    /// </returns>
    internal static bool ShouldRemove(Exception rejection, bool storedForThisTurn) =>
        storedForThisTurn || rejection is not IScreeningRefusal { IsVerdict: false };
}
