using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// A durable record of the ids an erasure request read for one person, so that a request that fails part-way can be
/// retried (#907).
/// </summary>
/// <remarks>
/// <para>
/// Several stores (observability sessions, tool-result spills, prompt usage, token budgets, the call-once ledger) carry
/// no owner, only a conversation or run id, and the only way to those ids is through the person's conversations and
/// plans. Erasure deletes those leads before the stage after them. When a store in that later stage fails, the next
/// request lists no conversations and no plans, and the failed store's rows are unreachable for good, while its line
/// reads <c>Erased</c> with nothing removed. The coordinator therefore records the ids here before deleting any lead,
/// merges them into every later request for the same person, and clears what it recorded only once every store that
/// reads them has succeeded.
/// </para>
/// <para>
/// The entries are themselves the person's data. Each carries the request that last wrote it, so a request clears only
/// its own, never one a concurrent request for the same person has just refreshed. Entries are also removed after the
/// configured journal retention, which must be at least as long as the longest retention among the stores they lead
/// to, so a purge never strands anything those stores still hold.
/// </para>
/// </remarks>
public interface IErasureJournal
{
    /// <summary>Everything recorded for <paramref name="ownerId"/> by earlier requests that did not finish.</summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded ids; empty when there are none.</returns>
    Task<ErasureJournalEntries> ReadAsync(string ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="entries"/> for <paramref name="ownerId"/> as written by <paramref name="requestId"/>. An id
    /// already recorded is taken over by this request and its time refreshed.
    /// </summary>
    /// <param name="ownerId">The person.</param>
    /// <param name="requestId">The erasure request writing the entries.</param>
    /// <param name="entries">The ids to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(string ownerId, string requestId, ErasureJournalEntries entries, CancellationToken cancellationToken);

    /// <summary>
    /// Removes what <paramref name="requestId"/> recorded for <paramref name="ownerId"/>, except withheld conversation ids,
    /// which age out on their own. Entries a later request has taken over are kept.
    /// </summary>
    /// <param name="ownerId">The person.</param>
    /// <param name="requestId">The request whose entries to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(string ownerId, string requestId, CancellationToken cancellationToken);
}
