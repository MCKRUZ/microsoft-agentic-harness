using Application.AI.Common.Models.Conversations;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Finds conversations nobody has written to for a while, so the retention sweep can expire them (#901). Implemented by
/// the conversation store, which is the only thing that knows when a conversation was last used.
/// </summary>
/// <remarks>
/// "Last written to" is the store's own update time, which every appended message moves forward. Reading a conversation
/// does not, so a conversation someone only looks at still ages.
/// </remarks>
public interface IIdleConversationSource
{
    /// <summary>
    /// Lists up to <paramref name="limit"/> conversations last written to before <paramref name="lastActivityBefore"/>,
    /// oldest first, every owner included.
    /// </summary>
    /// <param name="lastActivityBefore">The cutoff; a conversation written to at or after it is not idle.</param>
    /// <param name="limit">The most conversations to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The idle conversations, oldest first.</returns>
    Task<IReadOnlyList<IdleConversation>> ListIdleAsync(
        DateTimeOffset lastActivityBefore, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the conversation still exists and was last written to before <paramref name="lastActivityBefore"/>. Asked
    /// again once the sweep holds the conversation's turn lease, because a turn may have finished in between.
    /// </summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="lastActivityBefore">The cutoff.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when it exists and is still idle.</returns>
    Task<bool> IsIdleAsync(string conversationId, DateTimeOffset lastActivityBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the conversation, with its messages, only if it still belongs to <paramref name="ownerId"/> and was last
    /// written to before <paramref name="lastActivityBefore"/>, decided in the same step as the delete.
    /// </summary>
    /// <remarks>
    /// The expiry's own idle check runs earlier, before the stores reached through the conversation are cleared. Between
    /// the two, the owner can delete the conversation and someone can start a new one under the same id; a delete keyed on
    /// the id alone would then remove that new conversation. Owner erasure's <c>EraseSubjectAsync</c> deliberately has no
    /// such condition, so expiry does not use it.
    /// </remarks>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="ownerId">The owner the expiry listed it under, as issued.</param>
    /// <param name="lastActivityBefore">The cutoff.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> when this call deleted it.</returns>
    Task<bool> DeleteIfIdleAsync(
        string conversationId, string ownerId, DateTimeOffset lastActivityBefore, CancellationToken cancellationToken);
}
