using Domain.AI.Runs;

namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Durable storage for recurring schedules, and the single-machine claim-once primitive the tick
/// service uses to fire each one exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Async throughout, unlike <see cref="IRunJobStore"/>.</strong> The run substrate is
/// in-memory and its calls never await; a schedule store is genuinely backed by a database, so every
/// member here is.
/// </para>
/// <para>
/// <strong>The claim guarantee this interface documents is single-machine.</strong> The shipped
/// implementation (<c>EfScheduleStore</c>) uses SQLite optimistic concurrency, which is correct for
/// any number of processes sharing one machine's database file — the deployment this was built for.
/// A consumer needing a multi-machine guarantee implements this interface against a store with its
/// own distributed lock, without touching the tick service that calls it.
/// </para>
/// <para>
/// <strong>Ownership is checked by the store, not by its callers</strong> — the same discipline
/// <see cref="IRunJobStore"/> documents, for the same reason: every future surface that reads a
/// schedule must not have to remember to scope it itself.
/// </para>
/// </remarks>
public interface IScheduleStore
{
    /// <summary>Creates a new schedule.</summary>
    /// <param name="record">The schedule to store, already stamped with its owner and next fire time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateAsync(ScheduleRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a schedule visible to the caller, or <see langword="null"/> when it does not exist or
    /// belongs to someone else — the two are deliberately indistinguishable.
    /// </summary>
    /// <param name="scheduleId">The schedule to read.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScheduleRecord?> GetAsync(string scheduleId, string ownerId, string? tenantId, CancellationToken cancellationToken);

    /// <summary>Lists every schedule visible to the caller.</summary>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ScheduleRecord>> ListForOwnerAsync(string ownerId, string? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the caller's schedules without materializing them — the admission check
    /// <c>CreateScheduleCommandHandler</c> uses against <c>ScheduleConfig.MaxSchedulesPerOwner</c>,
    /// mirroring <c>IPlanStateStore.CountOwnedPlansAsync</c>'s same reasoning for plan quotas.
    /// </summary>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> CountForOwnerAsync(string ownerId, string? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <see cref="ScheduleRecord.Enabled"/> to <see langword="false"/>, guarded by the version the
    /// caller last read. Returns <see langword="false"/> when the schedule is missing, not visible to
    /// the caller, or has changed since it was read.
    /// </summary>
    /// <param name="scheduleId">The schedule to pause.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="expectedVersion">The <see cref="ScheduleRecord.Version"/> the caller last read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> PauseAsync(string scheduleId, string ownerId, string? tenantId, int expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <see cref="ScheduleRecord.Enabled"/> to <see langword="true"/>, guarded by the version the
    /// caller last read. Returns <see langword="false"/> when the schedule is missing, not visible to
    /// the caller, or has changed since it was read.
    /// </summary>
    /// <param name="scheduleId">The schedule to resume.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="expectedVersion">The <see cref="ScheduleRecord.Version"/> the caller last read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ResumeAsync(string scheduleId, string ownerId, string? tenantId, int expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a schedule. Returns <see langword="false"/> when it is missing or not visible to the
    /// caller.
    /// </summary>
    /// <param name="scheduleId">The schedule to delete.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DeleteAsync(string scheduleId, string ownerId, string? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every enabled schedule whose <see cref="ScheduleRecord.NextFireAt"/> is at or before
    /// <paramref name="now"/> — the tick service's own question, unscoped by caller for the same
    /// reason <see cref="IRunJobStore.GetParkedRuns"/> is.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ScheduleRecord>> GetDueSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims one tick of a due schedule: advances it to <paramref name="nextFireAt"/> and
    /// stamps <paramref name="firedAt"/>, but only if the stored row is still at
    /// <paramref name="expectedVersion"/>. Returns <see langword="true"/> to the winner and
    /// <see langword="false"/> to everyone else — including when the schedule no longer exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The durable counterpart of <see cref="IRunJobStore.TryBeginRun"/>: winning this call, not
    /// observing a schedule as due, is what entitles a caller to enqueue the run it fires.
    /// </para>
    /// <para>
    /// Returns a bare <see langword="bool"/>, matching <see cref="PauseAsync"/>/<see cref="ResumeAsync"/>,
    /// rather than the updated <see cref="ScheduleRecord"/> — deliberately, so the implementation can
    /// perform the conditional write as one round trip. Every field on the record besides
    /// <c>NextFireAt</c>/<c>LastFiredAt</c>/<c>Version</c> is unchanged by a claim, and the caller (which
    /// already holds the pre-claim record it read as due) can construct the post-claim record itself
    /// with a <c>with</c> expression — a second call re-reading the row would just be that same
    /// information, fetched a second time.
    /// </para>
    /// </remarks>
    /// <param name="scheduleId">The schedule to claim.</param>
    /// <param name="expectedVersion">The <see cref="ScheduleRecord.Version"/> read when the schedule was found due.</param>
    /// <param name="nextFireAt">The schedule's next fire time after this tick.</param>
    /// <param name="firedAt">Timestamp to record as this tick's fire time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TryClaimAsync(
        string scheduleId, int expectedVersion, DateTimeOffset nextFireAt, DateTimeOffset firedAt, CancellationToken cancellationToken);
}
