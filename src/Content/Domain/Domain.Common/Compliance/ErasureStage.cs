namespace Domain.Common.Compliance;

/// <summary>When, within one right-to-erasure request, a store is erased (#900).</summary>
public enum ErasureStage
{
    /// <summary>
    /// Stores found through the subject's conversations, or independent of them. Run first, while the conversations
    /// that lead to them still exist.
    /// </summary>
    BeforeConversations,

    /// <summary>
    /// The conversations themselves. Run only when every earlier store succeeded, so a failed request can be retried and
    /// still find what depends on them.
    /// </summary>
    Conversations,

    /// <summary>
    /// Records whose removal is only safe once the conversations are gone, such as the call-once ledger: deleting a
    /// surviving conversation's ledger rows would let a once-only tool run again in it. Run only when the conversations
    /// were erased.
    /// </summary>
    AfterConversations,
}
