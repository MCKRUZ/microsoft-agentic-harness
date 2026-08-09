using Domain.AI.KnowledgeGraph.Models;

namespace Domain.AI.Learnings;

/// <summary>
/// The recall-side half of the learnings trust contract: the single predicate deciding whether a
/// stored learning may be replayed back into an agent's context.
/// </summary>
/// <remarks>
/// The mirror of <c>KnowledgeMemoryService.IsRecallable</c> for the other memory channel, named to
/// match it so the two read alike. It has a single production caller today —
/// <c>RecallQueryHandler</c>, the recall chokepoint — and is named rather than inlined there for two
/// reasons: an inline <c>Trust == Trusted</c> at a read site reads as a filter, whereas a named
/// predicate reads as a rule, and the sibling channel arrived at three call sites by starting with
/// one. This is not the six-site consolidation the conversation-ownership check needed; it is the
/// cheaper habit of naming the invariant before it spreads.
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
