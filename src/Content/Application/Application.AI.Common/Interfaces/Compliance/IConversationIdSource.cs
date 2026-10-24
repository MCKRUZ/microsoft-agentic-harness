using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A conversation store's listing of one person's conversation ids for an erasure request (#907).
/// </summary>
/// <remarks>
/// <para>
/// Erasure needs only the ids, read before anything is deleted, because several stores carry nothing else that leads to
/// the person. <c>IConversationStore.ListAsync</c> serves the conversation list a person sees, so it loads every
/// message and skips a record it cannot read. Neither suits erasure: loading whole transcripts to collect ids makes an
/// erasure request as expensive as reading everything the person ever said, and a skipped record is one whose
/// conversation and everything it leads to would survive while the receipt read <c>Full</c>.
/// </para>
/// <para>
/// Implemented by the conversation stores that can erase. A store that does not implement it is listed through
/// <c>ListAsync</c>, as before.
/// </para>
/// </remarks>
public interface IConversationIdSource
{
    /// <summary>
    /// Lists the ids of <paramref name="ownerId"/>'s conversations, and counts the stored conversations that could not be
    /// read to find out whose they are.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id. Never blank.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids, and how many records could not be read.</returns>
    Task<ConversationIdListing> ListConversationIdsAsync(string ownerId, CancellationToken cancellationToken);
}
