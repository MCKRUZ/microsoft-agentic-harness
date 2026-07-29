using Domain.AI.Runs;

namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Holds queued and completed runs, and arms each one exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ownership is checked by the store, not by its callers.</strong> Every read takes the
/// caller's identity and answers as though another owner's run does not exist. Leaving that to
/// callers would mean every future surface — status, cancel, streaming — had to remember it, and the
/// one that forgot would be a cross-tenant read.
/// </para>
/// <para>
/// <strong><see cref="TryBeginRun"/> is the single-arming primitive.</strong> It moves a run from
/// queued to running and returns it only for the caller that won; everyone else gets
/// <see langword="null"/>. Without an atomic claim, a redelivered queue message or a second
/// dispatcher would run the same work twice, and duplicate execution here means duplicate model and
/// tool spend, not just a duplicate row.
/// </para>
/// </remarks>
public interface IRunJobStore
{
    /// <summary>Stores a newly accepted run. Throws when the job id already exists.</summary>
    /// <param name="record">The run to store, already stamped with its owner.</param>
    void Create(RunRecord record);

    /// <summary>
    /// Reads a run visible to <paramref name="ownerId"/>, or <see langword="null"/> when it does not
    /// exist, has expired, or belongs to someone else — the three are deliberately indistinguishable.
    /// </summary>
    /// <param name="jobId">The run to read.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    RunRecord? Get(string jobId, string ownerId);

    /// <summary>
    /// Atomically claims a queued run for execution, returning the updated record to the winner and
    /// <see langword="null"/> to everyone else.
    /// </summary>
    /// <remarks>
    /// Takes no owner: the dispatcher runs on a background thread with no caller attached, and the
    /// authorization that mattered happened when the run was accepted. The owner recorded on the
    /// run is what later reads are checked against.
    /// </remarks>
    /// <param name="jobId">The run to claim.</param>
    /// <param name="startedAt">Timestamp to record as the claim time.</param>
    RunRecord? TryBeginRun(string jobId, DateTimeOffset startedAt);

    /// <summary>
    /// Replaces a stored run. Returns <see langword="false"/> when no run with that id is held.
    /// </summary>
    /// <param name="record">The updated run.</param>
    bool Update(RunRecord record);

    /// <summary>
    /// Counts the runs <paramref name="ownerId"/> currently has queued or executing.
    /// </summary>
    /// <remarks>
    /// In-flight only — finished runs are history, not load. This is what bounds a caller's claim on
    /// the host at any instant, which neither the per-request rate limit nor any per-workflow ceiling
    /// expresses: a caller within both can otherwise start every workflow it owns at once, and each
    /// one then multiplies by its own parallel-step allowance.
    /// </remarks>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    int CountActiveRuns(string ownerId);

    /// <summary>Drops runs whose retention has elapsed, returning how many were removed.</summary>
    int SweepExpired();
}
