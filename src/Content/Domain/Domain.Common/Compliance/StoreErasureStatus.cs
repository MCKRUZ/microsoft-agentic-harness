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
}
