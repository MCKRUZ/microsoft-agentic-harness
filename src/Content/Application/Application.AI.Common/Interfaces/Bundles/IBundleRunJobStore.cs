using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Holds <see cref="BundleRunRecord"/>s — the async jobs created when a caller invokes a staged bundle —
/// keyed by job id, with a TTL. Bundle runs are not persisted (the host is not their system of record); a
/// record lives in memory just long enough for a caller to poll its result before it is swept.
/// </summary>
/// <remarks>
/// <para>
/// The record is immutable: a run is advanced by building a <c>with</c>-copy and calling <see cref="Update"/>,
/// which atomically swaps the stored snapshot.
/// </para>
/// <para>
/// <strong>Expiry contract (any implementation must honour this).</strong> Two — and only two — kinds of
/// record are reclaimable:
/// <list type="bullet">
///   <item><description>A <em>terminal</em> record, once its pollable window elapses. That window starts when
///   the run reaches a terminal state, so a completed run stays pollable for the configured retention window
///   regardless of how long it queued or ran.</description></item>
///   <item><description>An <em>unclaimed streaming reservation</em> — a <see cref="BundleRunStatus.Queued"/>
///   record with <see cref="BundleRunRecord.Streaming"/> set — once its (separate, shorter) connect window
///   elapses. Such a reservation has no background driver and may never be claimed, so it is reclaimed to bound
///   memory.</description></item>
/// </list>
/// Every other non-terminal record — a background-queued run awaiting the dispatcher, or any
/// <see cref="BundleRunStatus.Running"/> run (including a claimed streaming run) — is <em>never</em> expired,
/// so an in-flight run is never dropped nor its outcome lost to a mid-run sweep. An implementation that expired
/// a background-queued or running record would silently drop a run.
/// </para>
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
    /// Atomically transitions the run with <paramref name="jobId"/> from <see cref="BundleRunStatus.Queued"/> to
    /// <see cref="BundleRunStatus.Running"/>, stamping <paramref name="startedAt"/>, and returns the claimed
    /// snapshot. Returns null when the job id is unknown/swept or the run is no longer
    /// <see cref="BundleRunStatus.Queued"/> (already claimed by another driver or already terminal). This is the
    /// single point that guarantees a run is driven exactly once: both the background dispatcher and a live
    /// stream call it, so two drivers racing for the same job — two stream connections, or a stream and the
    /// dispatcher — cannot both begin it.
    /// </summary>
    /// <param name="jobId">The job id to claim.</param>
    /// <param name="startedAt">The timestamp to record as the run's start.</param>
    /// <returns>The claimed <see cref="BundleRunStatus.Running"/> snapshot, or null if it could not be claimed.</returns>
    BundleRunRecord? TryBeginRun(string jobId, DateTimeOffset startedAt);

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
