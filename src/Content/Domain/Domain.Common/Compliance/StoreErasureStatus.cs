namespace Domain.Common.Compliance;

/// <summary>What happened in one store during a right-to-erasure request (#900).</summary>
public enum StoreErasureStatus
{
    /// <summary>Everything the store held for the subject is gone; zero items is still this, when nothing was held.</summary>
    Erased,

    /// <summary>The store threw. What it held may remain; the receipt is <c>Partial</c>.</summary>
    Failed,

    /// <summary>
    /// Not attempted, because an earlier step failed and running this one would make a retry unable to find what is
    /// left. The conversations are kept when a store that is reached through them fails.
    /// </summary>
    Skipped,

    /// <summary>
    /// The store this host is configured with holds personal data but cannot erase one person's, typically because a
    /// deployer replaced a shipped store with one that does not implement erasure. The receipt is <c>Partial</c>; a retry
    /// will not help, so it does not hold later stages back.
    /// </summary>
    NotSupported,

    /// <summary>
    /// A store that cannot be searched for one person, such as logs, traces or the response cache, and that nothing ages
    /// out. It may still hold the subject's data with no end date, so the receipt is <c>Partial</c> while it is listed.
    /// </summary>
    RetainedIndefinitely,

    /// <summary>
    /// A store that cannot be searched for one person but whose records go away on their own, such as one held only in
    /// process memory. A retention period that configuration can switch off does not count. Listed so the subject knows;
    /// it does not make the receipt <c>Partial</c>.
    /// </summary>
    RetainedUntilExpiry,

    /// <summary>
    /// A tamper-evident accountability record kept on purpose (the audit trails), which cannot be altered to remove one
    /// person without breaking the evidence it exists to provide. Its line names the kinds of data it holds. It is the one
    /// store kept with no end date that does not make the receipt <c>Partial</c>: <c>Full</c> means every other store is
    /// clear of the subject, and says so of the audit trails only through their listed lines.
    /// </summary>
    RetainedByObligation,

    /// <summary>
    /// The organisation's own business content: documents other people loaded, and what was drawn from them, which may
    /// mention the subject (#901). The subject's own uploads are erased by owner; these are kept until the organisation
    /// removes them, or until the optional document age limit does. Listed with that reason so the subject knows; it does
    /// not make the receipt <c>Partial</c>, because keeping them is the organisation's decision about its own records,
    /// not a store failing to find the subject.
    /// </summary>
    RetainedAsBusinessContent,
}
