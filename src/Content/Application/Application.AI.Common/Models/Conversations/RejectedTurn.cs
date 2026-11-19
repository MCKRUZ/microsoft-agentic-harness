namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// What <see cref="Services.RejectedTurnMessage.RejectAsync"/> did with a turn rejected before the model saw it:
/// what the caller is told, and whether the caller's stored message is gone.
/// </summary>
/// <remarks>
/// The interactive clients show the caller's message as soon as it is sent, before the turn runs. When the store drops
/// a rejected message, a client that is not told keeps showing text the conversation no longer holds, and a later
/// retry or edit keyed by that message's id finds nothing (#1036). <see cref="RemovedMessageId"/> is how a transport
/// tells it.
/// </remarks>
public sealed record RejectedTurn
{
    /// <summary>What the caller is told. Never contains the rejected text.</summary>
    public required string ClientMessage { get; init; }

    /// <summary>
    /// The id of the message the store no longer holds, or <see langword="null"/> when it still holds it: a retried
    /// message kept through a screening outage, or a removal that failed.
    /// </summary>
    public Guid? RemovedMessageId { get; init; }
}
