using Domain.AI.KnowledgeGraph.Models;

namespace Domain.AI.Learnings;

/// <summary>
/// The recall-side half of the learnings trust contract: the single predicate deciding whether a
/// stored learning may be replayed back into an agent's context.
/// </summary>
/// <remarks>
/// The mirror of <c>KnowledgeMemoryService.IsRecallable</c> for the other memory channel, and named
/// to match it. It exists as one shared predicate rather than an inline comparison at each read site
/// for the reason the conversation-ownership check was consolidated: a trust rule hand-written at
/// several call sites is a trust rule that eventually differs at one of them.
/// </remarks>
public static class LearningEntryTrustExtensions
{
    /// <summary>
    /// Whether <paramref name="entry"/> may be returned by recall and injected into agent context.
    /// Quarantined (<see cref="MemoryTrust.Untrusted"/>) entries are retained in the store for audit
    /// and incident response but are never served back to an agent.
    /// </summary>
    /// <param name="entry">The stored learning being considered for recall.</param>
    public static bool IsRecallable(this LearningEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Trust == MemoryTrust.Trusted;
    }
}
