namespace Domain.AI.Bundles;

/// <summary>
/// The in-memory, TTL'd record of one bundle run — the async job created when a caller invokes a staged
/// bundle. It carries both the run's <em>inputs</em> (the handle it targets, the resolved capability
/// envelope, and the conversation request) and its evolving <em>state</em> (status, outcome, timestamps),
/// so the background dispatcher can pick a run up by id alone, re-arm the ambient envelope on its detached
/// thread, and drive it to completion.
/// </summary>
/// <remarks>
/// <para>
/// The host is not the system of record for bundle runs: records live only in memory and are evicted by
/// TTL. A record is created <see cref="BundleRunStatus.Queued"/>, transitions once to
/// <see cref="BundleRunStatus.Running"/> when the dispatcher picks it up, and once more to a terminal
/// <see cref="BundleRunStatus.Succeeded"/>/<see cref="BundleRunStatus.Failed"/>. It is immutable; each
/// transition is a <c>with</c>-copy the store swaps in.
/// </para>
/// <para>
/// <strong>Why the envelope rides on the record.</strong> The background dispatcher runs on a thread pool
/// flow detached from the request that enqueued the run, so the ambient <c>CapabilityEnvelope</c> published
/// during the HTTP request does not flow to it. The resolved grant is therefore captured here at enqueue
/// time and re-published by the dispatcher (<c>CapabilityEnvelopeAccessor.Begin</c>) for the duration of the
/// run — without it the governor would see no envelope and fail open. The staged bundle backing
/// <see cref="Handle"/> supplies the ephemeral-agent overlay the same way (looked up by handle at dispatch),
/// so the overlay is not duplicated here.
/// </para>
/// </remarks>
public sealed record BundleRunRecord
{
    /// <summary>Opaque unique identifier for this run, returned to the caller as the job id to poll.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The handle of the staged bundle this run targets. The dispatcher looks the staged bundle up by this
    /// handle to build the ephemeral-agent overlay; a run whose handle has expired fails cleanly.
    /// </summary>
    public required string Handle { get; init; }

    /// <summary>
    /// Stable identifier of the caller that created this run. Only this owner may poll the run; the poll
    /// query rejects a mismatch as not found, so a run's result cannot be read across callers even if the
    /// job id leaks.
    /// </summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// The ephemeral agent's id (its <c>AGENT.md</c> id), captured from the staged bundle at enqueue time
    /// so the dispatcher can name it to <c>RunConversationCommand</c> without re-reading the bundle.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>The user messages seeding the conversation. One turn per message, bounded by <see cref="MaxTurns"/>.</summary>
    public required IReadOnlyList<string> UserMessages { get; init; }

    /// <summary>The maximum number of turns the conversation may run.</summary>
    public required int MaxTurns { get; init; }

    /// <summary>
    /// The per-caller capability grant resolved for this run. The dispatcher re-publishes it ambiently for
    /// the whole run so the permission gate chain and tool-chain builder confine the ephemeral agent.
    /// </summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>The current lifecycle state. Starts <see cref="BundleRunStatus.Queued"/>.</summary>
    public required BundleRunStatus Status { get; init; }

    /// <summary>
    /// The run's dispatch mode. When false (the default) the run is background-dispatched: it is enqueued and a
    /// worker drives it to completion for the caller to poll. When true the run is <em>externally driven</em>
    /// instead — it is created <see cref="BundleRunStatus.Queued"/> but not enqueued, and whoever holds the run
    /// (the live-stream transport) claims it (<see cref="BundleRunStatus.Queued"/> →
    /// <see cref="BundleRunStatus.Running"/>) and drives it, binding the run's lifetime to that consumer.
    /// Because such a reservation may never be claimed (the consumer might never attach), an unclaimed
    /// externally-driven run is the one non-terminal record the job store may reclaim on TTL — every other
    /// non-terminal record is retained until it completes.
    /// </summary>
    public bool Streaming { get; init; }

    /// <summary>The terminal outcome once the run has <see cref="BundleRunStatus.Succeeded"/>; null before then.</summary>
    public BundleRunOutcome? Outcome { get; init; }

    /// <summary>
    /// A stable, caller-safe reason when the run <see cref="BundleRunStatus.Failed"/> outright; null
    /// otherwise. Never a raw exception message — the dispatcher logs the exception and stores a scrubbed code.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>When the run record was created and enqueued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the dispatcher began executing the run; null while still <see cref="BundleRunStatus.Queued"/>.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run reached a terminal state; null before then.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Whether the run has reached a terminal state and will not transition again.</summary>
    public bool IsTerminal => Status is BundleRunStatus.Succeeded or BundleRunStatus.Failed;
}
