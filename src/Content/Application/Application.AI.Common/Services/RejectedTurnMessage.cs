using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.Common.Exceptions.ExceptionTypes;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services;

/// <summary>
/// What a transport tells the caller about a turn rejected before the model saw it, and the one case where a rejection
/// still removes a stored message (#959, #964).
/// </summary>
/// <remarks>
/// <para>
/// The interactive transports store the caller's message only once the turn is admitted (<see cref="AgentTurnAdmission"/>),
/// so a rejected send or edit never stored anything and needs no cleanup. Which outcome a transport reports follows from
/// whether the turn was admitted, not from the exception type; this type only supplies the caller-facing text and keeps
/// a rejection off the agent's health record.
/// </para>
/// <para>
/// A retry is the exception: it re-sends a message an earlier turn stored. A verdict against it now (detection since
/// tightened) means it should not stay in the history either; a screening outage is no verdict, so it stays.
/// </para>
/// </remarks>
public static class RejectedTurnMessage
{
    /// <summary>What the caller is told when validation rejects the message.</summary>
    public const string NotAcceptedMessage = "This message could not be accepted. Check its length and try again.";

    /// <summary>Whether <paramref name="ex"/> is a request rejection rather than the agent failing.</summary>
    /// <param name="ex">The exception the turn dispatch threw.</param>
    /// <returns><see langword="true"/> for a screening refusal or a validation rejection.</returns>
    public static bool IsRejection(Exception ex) => ex is IScreeningRefusal or ValidationException;

    /// <summary>What the caller is told about a rejection. Never contains the rejected text.</summary>
    /// <param name="rejection">The rejection, as recognised by <see cref="IsRejection"/>.</param>
    /// <returns>The refusal's own message, or <see cref="NotAcceptedMessage"/> for a validation rejection.</returns>
    public static string ClientMessageFor(Exception rejection) =>
        rejection is IScreeningRefusal refusal ? refusal.ClientMessage : NotAcceptedMessage;

    /// <summary>Whether a rejection of a retried, already stored message removes it.</summary>
    /// <param name="rejection">The rejection, as recognised by <see cref="IsRejection"/>.</param>
    /// <returns><see langword="false"/> only for a screening outage, which says nothing about the text.</returns>
    public static bool RemovesRetriedMessage(Exception rejection) =>
        rejection is not IScreeningRefusal { IsVerdict: false };

    /// <summary>
    /// Removes a retried message the rejection says should go, with everything after it (the answer the retry was
    /// going to replace).
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="conversationId">The conversation the message is stored in.</param>
    /// <param name="callerId">The authenticated caller; the store refuses another owner's conversation.</param>
    /// <param name="messageId">The retried message.</param>
    /// <param name="logger">Where a failed removal is reported.</param>
    /// <returns>The conversation after the removal, or <see langword="null"/> when nothing was removed.</returns>
    /// <remarks>
    /// Takes no cancellation token on purpose: a caller disconnecting after the rejection must not leave the message in
    /// the history. Best effort: a failure is logged as the replay risk it is.
    /// </remarks>
    public static async Task<ConversationRecord?> RemoveRetriedMessageAsync(
        IConversationStore store, string conversationId, string callerId, Guid messageId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await store.TruncateFromMessageAsync(conversationId, callerId, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to remove the rejected retry from conversation {ConversationId}; it will be replayed as history.",
                conversationId);
            return null;
        }
    }
}
