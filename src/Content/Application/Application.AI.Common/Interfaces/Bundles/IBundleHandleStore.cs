using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Holds the <see cref="StagedBundle"/>s that have been accepted and extracted to disk, keyed by an opaque
/// handle, and owns their on-disk lifetime: it deletes a bundle's staging directory when the handle expires,
/// is explicitly removed, or the host shuts down. This is the store behind <c>register → handle → invoke</c>
/// — the host is not the system of record for bundles, so a handle is short-lived and TTL'd.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime is sliding and run-pinned.</strong> A handle's expiry is pushed out whenever it is read
/// (<see cref="TryGet"/>) or a run acquires it (<see cref="Acquire"/>). A run in flight <em>pins</em> the
/// handle through the lease returned by <see cref="Acquire"/>, so the cleanup sweeper can never delete a
/// staging directory out from under an executing run — the ephemeral agent reads its skills from that
/// directory on disk throughout the turn. The directory is deleted only once the handle is both expired
/// (or removed) and has no outstanding lease.
/// </para>
/// <para>
/// <strong>Guaranteed cleanup.</strong> Deletion happens on three paths: an explicit <see cref="Remove"/>,
/// the periodic <see cref="SweepExpired"/> pass, and host shutdown disposal. The sweeper is the
/// belt-and-suspenders backstop that guarantees an abandoned staging directory is removed even if no caller
/// ever deletes the handle. Implementations are process-local and non-durable: handles do not survive a
/// restart, but a restart also loses the in-memory job records that reference them, so the two stay
/// consistent (an orphaned directory left by a crash is swept by the same pass on the next start).
/// </para>
/// </remarks>
public interface IBundleHandleStore
{
    /// <summary>
    /// Registers a freshly staged bundle under an owning caller and returns the opaque handle used to run or
    /// delete it. The handle is bound to <paramref name="ownerId"/>; callers verify ownership via
    /// <see cref="GetOwner"/> before acting on a handle, so a leaked handle cannot be used across callers. The
    /// handle's initial expiry is <c>now + HandleTtl</c>.
    /// </summary>
    /// <param name="bundle">The staged bundle to hold. Its <see cref="StagedBundle.StagedRootDirectory"/> becomes owned by this store.</param>
    /// <param name="ownerId">The stable identifier of the caller that owns this handle.</param>
    /// <returns>The handle identifying the registered bundle.</returns>
    string Register(StagedBundle bundle, string ownerId);

    /// <summary>
    /// Returns the owner the handle was registered under, or null when the handle is unknown or expired.
    /// Callers compare this against the requesting caller to enforce per-owner isolation before running,
    /// reading, or deleting a bundle. Does not refresh the sliding expiry — it is an authorization check, not
    /// a use of the handle.
    /// </summary>
    /// <param name="handle">The handle to look up.</param>
    /// <returns>The owner id, or null if the handle is unknown or expired.</returns>
    string? GetOwner(string handle);

    /// <summary>
    /// Reads the staged bundle for <paramref name="handle"/> without pinning it, refreshing its sliding
    /// expiry. Returns null when the handle is unknown or already expired. Use this for a read-only peek
    /// (e.g. to name the agent when enqueuing a run); use <see cref="Acquire"/> to actually execute against it.
    /// </summary>
    /// <param name="handle">The handle to look up.</param>
    /// <returns>The staged bundle, or null if the handle is unknown or expired.</returns>
    StagedBundle? TryGet(string handle);

    /// <summary>
    /// Acquires <paramref name="handle"/> for the duration of a run: refreshes its sliding expiry, pins it
    /// so the sweeper cannot delete its staging directory while the returned lease is held, and returns a
    /// lease carrying the staged bundle. Returns null when the handle is unknown or already expired.
    /// Dispose the lease when the run completes to unpin the handle.
    /// </summary>
    /// <param name="handle">The handle to acquire.</param>
    /// <returns>A lease over the staged bundle, or null if the handle is unknown or expired.</returns>
    IBundleHandleLease? Acquire(string handle);

    /// <summary>
    /// Explicitly removes <paramref name="handle"/>. The staging directory is deleted immediately if no run
    /// currently holds a lease on it; otherwise deletion is deferred until the last lease is released, so an
    /// in-flight run is never disrupted. Returns true if a handle was present to remove.
    /// </summary>
    /// <param name="handle">The handle to remove.</param>
    /// <returns>True if the handle existed and was removed; false if it was already gone.</returns>
    bool Remove(string handle);

    /// <summary>
    /// Deletes every handle whose sliding expiry is at or before now and that has no outstanding lease,
    /// removing each one's staging directory. Called periodically by the cleanup sweeper. Returns the number
    /// of handles evicted.
    /// </summary>
    /// <returns>The count of handles evicted and whose staging directories were deleted.</returns>
    int SweepExpired();
}

/// <summary>
/// A pin on a staged-bundle handle held for the duration of a run. While the lease is undisposed, the
/// handle store will not delete the bundle's staging directory, so the ephemeral agent can read its skills
/// from disk throughout the run. Disposing the lease unpins the handle and refreshes its sliding expiry.
/// </summary>
public interface IBundleHandleLease : IDisposable
{
    /// <summary>The staged bundle pinned by this lease.</summary>
    StagedBundle Bundle { get; }
}
