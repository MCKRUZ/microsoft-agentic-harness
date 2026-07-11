using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Bundles;

/// <summary>
/// Drives a single staged-bundle run to a terminal state under its own capability envelope and
/// ephemeral-agent overlay. This is the one place the security-critical ambient arming lives, so both
/// triggers of a bundle run share it: the background dispatcher (for async, poll-only runs) and the live
/// Server-Sent-Events stream endpoint (for opt-in streaming runs).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it does.</strong> Given a job id, it acquires a lease on the run's staged bundle (pinning the
/// staging directory against the cleanup sweeper for the run's duration), atomically claims the run
/// (<see cref="BundleRunStatus.Queued"/> → <see cref="BundleRunStatus.Running"/>), re-publishes the run's
/// capability envelope and ephemeral-agent overlay ambiently, drives a full multi-turn conversation, maps the
/// result onto the run record, and releases the lease — recording a terminal state on every path.
/// </para>
/// <para>
/// <strong>Why the envelope arming lives here.</strong> The tool-invocation governor enforces the per-caller
/// grant only when an envelope is published ambiently for the running turn; with none it fails <em>open</em>.
/// The background dispatcher runs on a thread detached from the originating request and the stream endpoint
/// runs on the connection thread, so in both cases the envelope must be re-published around the drive. Keeping
/// that arming in a single implementation is what stops one trigger from silently diverging and opening the
/// gate. The envelope scope stays open across the entire drive: the conversation is fully materialised before
/// this method returns (deltas are pushed out-of-band through the ambient turn-stream sink while it runs), so
/// there is no deferred enumeration that could outlive the scope.
/// </para>
/// <para>
/// <strong>Streaming.</strong> This method is transport-agnostic: it never touches the HTTP response. A
/// streaming caller arms the ambient <c>AgentTurnStreamSink</c> before calling it, so assistant text deltas
/// flow to that caller's sink as the conversation runs; a non-streaming caller (the dispatcher) arms no sink
/// and the same drive runs blocking. Either way the terminal outcome is recorded identically.
/// </para>
/// <para>
/// <strong>Ownership is not checked here.</strong> The executor authorizes nothing — it drives whatever job id
/// it is given. Callers are responsible for having verified that the requesting principal owns the run (the
/// dispatcher only ever runs records it created; the stream endpoint owner-checks via the poll query before
/// calling). Do not expose this to an unauthenticated surface.
/// </para>
/// </remarks>
public interface IBundleRunExecutor
{
    /// <summary>
    /// Executes the run with <paramref name="jobId"/> to a terminal state, or reports why it could not begin.
    /// </summary>
    /// <param name="jobId">The id of the queued run to drive.</param>
    /// <param name="cancellationToken">
    /// Cancels the run. For a stream this is the connection's abort token, so a client disconnect ends the run.
    /// </param>
    /// <returns>The outcome of the attempt — see <see cref="BundleRunExecution"/>.</returns>
    Task<BundleRunExecution> ExecuteAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>
/// The result of an <see cref="IBundleRunExecutor.ExecuteAsync"/> attempt: whether this call drove the run and,
/// when it did, the terminal record snapshot so a streaming caller can emit the right closing event.
/// </summary>
public sealed record BundleRunExecution
{
    /// <summary>What happened to the run when this call tried to drive it.</summary>
    public required BundleRunExecutionStatus Status { get; init; }

    /// <summary>
    /// The run record. The terminal snapshot when <see cref="Status"/> is <see cref="BundleRunExecutionStatus.Ran"/>
    /// (inspect its <see cref="BundleRunRecord.Status"/>/<see cref="BundleRunRecord.Outcome"/>); the
    /// last-observed snapshot for <see cref="BundleRunExecutionStatus.AlreadyClaimed"/> (carried for context
    /// only — a caller that lost the claim race owns nothing about the run); null for
    /// <see cref="BundleRunExecutionStatus.NotFound"/>.
    /// </summary>
    public BundleRunRecord? Record { get; init; }

    /// <summary>Convenience result for a job id that had no record.</summary>
    public static BundleRunExecution NotFound { get; } = new() { Status = BundleRunExecutionStatus.NotFound };

    /// <summary>Builds an <see cref="BundleRunExecutionStatus.AlreadyClaimed"/> result carrying the current snapshot.</summary>
    public static BundleRunExecution AlreadyClaimed(BundleRunRecord? current) =>
        new() { Status = BundleRunExecutionStatus.AlreadyClaimed, Record = current };

    /// <summary>Builds a <see cref="BundleRunExecutionStatus.Ran"/> result carrying the terminal snapshot.</summary>
    public static BundleRunExecution Ran(BundleRunRecord terminal) =>
        new() { Status = BundleRunExecutionStatus.Ran, Record = terminal };
}

/// <summary>Whether an <see cref="IBundleRunExecutor.ExecuteAsync"/> call drove the run, and if not, why.</summary>
public enum BundleRunExecutionStatus
{
    /// <summary>No run record exists for the job id (never created, or already swept).</summary>
    NotFound = 0,

    /// <summary>
    /// The run was not <see cref="BundleRunStatus.Queued"/> when this call tried to claim it — another driver
    /// already began it, or it had already reached a terminal state. This call drove nothing.
    /// </summary>
    AlreadyClaimed = 1,

    /// <summary>
    /// This call drove the run to a terminal state. The carried record's own
    /// <see cref="BundleRunRecord.Status"/> distinguishes a completed conversation from a failed one.
    /// </summary>
    Ran = 2,
}
