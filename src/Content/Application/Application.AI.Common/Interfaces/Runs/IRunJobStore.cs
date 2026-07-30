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
/// <para>
/// <strong>Admission is decided here too, for the same reason.</strong> Both limits on accepting a
/// run — one live run per target, and a ceiling on what one caller may have in flight — are
/// read-then-write decisions. Deciding them in a handler and inserting afterwards leaves a window in
/// which concurrent requests all observe the limit as unmet and all proceed.
/// </para>
/// </remarks>
public interface IRunJobStore
{
    /// <summary>
    /// Admits a newly accepted run, or refuses it, deciding and inserting as one atomic step.
    /// </summary>
    /// <param name="record">The run to store, already stamped with its owner.</param>
    /// <param name="maxActiveRunsPerOwner">
    /// How many runs one owner may have queued or executing at once. Supplied by the caller rather
    /// than read here, because it is host policy rather than a property of storage.
    /// </param>
    /// <returns>Whether the run was admitted, and if not, which limit refused it.</returns>
    /// <exception cref="InvalidOperationException">The job id is already held.</exception>
    RunAdmission TryCreate(RunRecord record, int maxActiveRunsPerOwner);

    /// <summary>
    /// Reads a run visible to the caller, or <see langword="null"/> when it does not exist, has
    /// expired, or belongs to someone else — the three are deliberately indistinguishable.
    /// </summary>
    /// <remarks>
    /// Scoped by tenant as well as owner, matching how plan ownership is decided on the same request
    /// path. A single-tenant issuer makes the owner alone sufficient today, so the tenant leg buys
    /// nothing until a host accepts tokens from a second tenant — at which point its absence would be
    /// a cross-tenant read, and the record already carries what is needed to prevent it.
    /// </remarks>
    /// <param name="jobId">The run to read.</param>
    /// <param name="ownerId">Stable identity of the calling principal.</param>
    /// <param name="tenantId">Tenant of the calling principal, when the host resolves one.</param>
    RunRecord? Get(string jobId, string ownerId, string? tenantId);

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
    /// Finds the live run against <paramref name="targetId"/>, or <see langword="null"/> when there
    /// is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answerable at all only because admission permits one live run per target: without that rule
    /// this question would have no single answer, and progress reported against a target could not be
    /// attributed to a particular run.
    /// </para>
    /// <para>
    /// Takes no caller and applies no scope. It exists for work already executing under an identity
    /// the host established, which needs to know which run it is — not for answering a caller. Nothing
    /// reached through a request may use it to resolve a run it was not given the id of.
    /// </para>
    /// </remarks>
    /// <param name="kind">The kind of work.</param>
    /// <param name="targetId">The thing being run.</param>
    RunRecord? FindLiveRunForTarget(RunKind kind, string targetId);

    /// <summary>
    /// Lists the runs currently parked awaiting a decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes no caller and applies no scope, for the same reason as
    /// <see cref="FindLiveRunForTarget"/>: it answers the host's own question — which parked work can
    /// now continue — rather than any caller's. Nothing reached through a request may use it.
    /// </para>
    /// <para>
    /// Returns the records, not their identifiers, because the caller's next question is what each run
    /// is waiting on. Handing back ids would make the caller read each one again through a scoped
    /// accessor it has no identity for.
    /// </para>
    /// </remarks>
    IReadOnlyList<RunRecord> GetParkedRuns();

    /// <summary>
    /// Atomically returns a parked run to the queue, handing the updated record to the caller that won
    /// and <see langword="null"/> to everyone else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="TryBeginRun"/>, and atomic for the same reason: two resumers that
    /// both observed the run as parked would both enqueue it, and the second dispatch would execute the
    /// same plan concurrently with the first. Winning the transition — not observing the verdict — is
    /// what entitles a caller to enqueue.
    /// </para>
    /// <para>
    /// <see cref="RunRecord.ParkedAt"/> and <see cref="RunRecord.AwaitingEscalationIds"/> are cleared:
    /// both describe a wait that is over. Leaving them would make a run that parks again look like it
    /// had been waiting since its first gate, and would keep it eligible for the parked-run ceiling
    /// while it is running.
    /// </para>
    /// </remarks>
    /// <param name="jobId">The parked run to return to the queue.</param>
    RunRecord? TryResume(string jobId);

    /// <summary>
    /// Atomically cancels a run that has not started executing, or is parked awaiting a decision,
    /// returning the run <em>as it stood immediately before</em> the transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous record is returned, not the updated one, because what the caller needs next is
    /// what the run was doing — specifically which decisions it was parked on, so those can be
    /// withdrawn. Reading that separately beforehand would not be the same thing: a run can park
    /// between the read and the cancel, and the caller would then withdraw nothing while believing it
    /// had.
    /// </para>
    /// <para>
    /// <strong>A run that is executing is deliberately not cancelled here.</strong> Its status is
    /// owned by the dispatch in flight, which will write a terminal state when the work stops; writing
    /// one here would be overwritten by it moments later. Stopping executing work is a matter of
    /// signalling it, which is a different mechanism from storage.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when there is no such run, or when it is executing or already
    /// terminal — the three being distinguishable by the caller from a prior scoped read, which this
    /// is not a substitute for.
    /// </para>
    /// </remarks>
    /// <param name="jobId">The run to cancel.</param>
    /// <param name="cancelledAt">Timestamp to record as the completion time.</param>
    RunRecord? TryCancel(string jobId, DateTimeOffset cancelledAt);

    /// <summary>
    /// Fails runs that have been parked awaiting a decision for longer than
    /// <paramref name="maxParkedDuration"/>, returning the identifiers of those failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parked run is live, so retention never reclaims it and its target stays locked. That is the
    /// point — a workflow waiting on an approval is genuinely still in flight — but it means an
    /// unanswered gate would otherwise hold a workflow and an owner's concurrency slot for the life of
    /// the process. Failing the run makes it terminal, which lets both go.
    /// </para>
    /// <para>
    /// Fails rather than deletes. A caller that comes back to a run it started is owed an answer about
    /// what became of it, and "no such run" is the one answer that tells it nothing.
    /// </para>
    /// <para>
    /// Returns each run <em>as it stood immediately before</em> being failed, for the same reason
    /// <see cref="TryCancel"/> does: giving up on a run has to release what it was holding, and the
    /// approvals it was parked on are part of that. A caller handed only identifiers could not withdraw
    /// them, and an approval that outlives its run is answerable by someone who cannot know it is moot
    /// — worse here than on the cancel path, because failing the run unlocks its workflow, so the next
    /// run inherits the plan the stale verdict will be reconciled against.
    /// </para>
    /// </remarks>
    /// <param name="maxParkedDuration">How long a run may stay parked before it is given up on.</param>
    IReadOnlyList<RunRecord> ExpireStaleParkedRuns(TimeSpan maxParkedDuration);

    /// <summary>
    /// Drops runs whose retention has elapsed, returning the identifiers of those removed.
    /// </summary>
    /// <remarks>
    /// Reports which runs went, not merely how many. A run's records are not the only thing keyed by
    /// its identifier — progress bookkeeping is too — and a sweep that returned a count would leave
    /// every other holder guessing which entries it may now release.
    /// </remarks>
    IReadOnlyList<string> SweepExpired();
}
