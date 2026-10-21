namespace Domain.Common.Compliance;

/// <summary>One store's line on an erasure receipt (#900).</summary>
/// <param name="Store">The store, as a reviewer would name it (for example <c>conversations</c>).</param>
/// <param name="Status">What happened.</param>
/// <param name="ItemsRemoved">How many records the store reports it actually removed; never a requested count.</param>
/// <param name="Reason">
/// Why the status is not <see cref="StoreErasureStatus.Erased"/>; a stable, scrubbed phrase, never exception text,
/// because receipts are returned to the caller.
/// </param>
public sealed record StoreErasureOutcome(string Store, StoreErasureStatus Status, int ItemsRemoved, string? Reason = null)
{
    /// <summary>A store that removed <paramref name="itemsRemoved"/> records, possibly zero.</summary>
    public static StoreErasureOutcome Erased(string store, int itemsRemoved) =>
        new(store, StoreErasureStatus.Erased, itemsRemoved);
}
