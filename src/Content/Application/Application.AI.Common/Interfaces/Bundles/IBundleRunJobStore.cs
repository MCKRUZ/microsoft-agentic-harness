using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Holds <see cref="BundleRunRecord"/>s — the async jobs created when a caller invokes a staged bundle —
/// keyed by job id, with a TTL. Bundle runs are not persisted (the host is not their system of record); a
/// record lives in memory just long enough for a caller to poll its result before it is swept.
/// </summary>
/// <remarks>
/// The record is immutable: the dispatcher advances a run by building a <c>with</c>-copy and calling
/// <see cref="Update"/>, which atomically swaps the stored snapshot. Only a terminal record has a TTL — it
/// starts when the run reaches a terminal state, so a completed run stays pollable for the configured window
/// regardless of how long it queued or ran. A <see cref="BundleRunStatus.Queued"/>/<see cref="BundleRunStatus.Running"/>
/// record is in-flight and is never expired, so a run is never dropped or its outcome lost to a mid-run sweep.
/// </remarks>
public interface IBundleRunJobStore
{
    /// <summary>
    /// Stores a newly created run record and starts its TTL. The record is expected to be
    /// <see cref="BundleRunStatus.Queued"/>. Throws if a record with the same job id already exists.
    /// </summary>
    /// <param name="record">The run record to store.</param>
    void Create(BundleRunRecord record);

    /// <summary>
    /// Returns the current snapshot of the run with <paramref name="jobId"/>, or null when the job id is
    /// unknown or its record has already been swept.
    /// </summary>
    /// <param name="jobId">The job id to look up.</param>
    /// <returns>The current record snapshot, or null.</returns>
    BundleRunRecord? Get(string jobId);

    /// <summary>
    /// Replaces the stored snapshot for <paramref name="record"/>'s job id. When the record is terminal its
    /// TTL is extended so the completed result stays pollable. Returns false when the job id is no longer
    /// present (already swept), in which case the update is dropped.
    /// </summary>
    /// <param name="record">The new snapshot to store.</param>
    /// <returns>True if the record was present and updated; false if it had already been swept.</returns>
    bool Update(BundleRunRecord record);

    /// <summary>
    /// Removes every run record whose TTL has elapsed. Called periodically by the cleanup sweeper. Returns
    /// the number of records evicted.
    /// </summary>
    /// <returns>The count of records evicted.</returns>
    int SweepExpired();
}
