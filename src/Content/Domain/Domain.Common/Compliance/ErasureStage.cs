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
    /// The plans and their run records, which lead to the step sessions of the subject's planned tasks the way
    /// conversations lead to theirs (#906). Run only when every earlier store succeeded. A separate stage from the
    /// conversations so each lead's success is known on its own: the stage after acts only on what an erased lead led to.
    /// </summary>
    PlannedTasks,

    /// <summary>
    /// Records whose removal is only safe once what leads to them is gone, such as the call-once ledger: deleting a
    /// surviving conversation's ledger rows would let a once-only tool run again in it. Run for the conversations' ids when
    /// the conversations were erased, and for the planned tasks' ids when the plans were.
    /// </summary>
    AfterConversations,
}
