namespace Domain.AI.Bundles;

/// <summary>
/// The lifecycle state of a single bundle run — the async job created when a caller invokes a staged
/// bundle and consumed by pollers until it reaches a terminal state. A run is created <see cref="Queued"/>,
/// picked up by the background dispatcher into <see cref="Running"/>, and ends in exactly one of
/// <see cref="Succeeded"/> or <see cref="Failed"/>.
/// </summary>
/// <remarks>
/// <para>
/// The numeric values are anchored by a Domain.AI enum test: run records are in-memory and TTL'd (the host
/// is not the system of record for bundle runs), but a caller polling <c>GET .../runs/{jobId}</c> compares
/// against these values, so renumbering them would silently change the wire contract. Add new states only
/// at the end.
/// </para>
/// <para>
/// <see cref="Succeeded"/> and <see cref="Failed"/> are terminal: once a run reaches either, the background
/// service performs no further transition and the record is only read (by the poll query) until its TTL
/// evicts it. <see cref="Succeeded"/> reflects that the conversation ran to completion; a conversation that
/// completed but whose agent reported a turn failure is still surfaced as <see cref="Succeeded"/> with the
/// failure carried in the result, mirroring how <c>RunConversationCommand</c> distinguishes a failed turn
/// (a returned result) from an unhandled exception (a thrown escape) — only the latter is <see cref="Failed"/>.
/// </para>
/// </remarks>
public enum BundleRunStatus
{
    /// <summary>
    /// The run has been created and enqueued but the background dispatcher has not yet picked it up. This
    /// is the initial state of every run record.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// The background dispatcher has dequeued the run and is executing the bundle's ephemeral agent
    /// conversation under the run's capability envelope. Transitions to <see cref="Succeeded"/> or
    /// <see cref="Failed"/> when the conversation returns or throws.
    /// </summary>
    Running = 1,

    /// <summary>
    /// Terminal — the bundle conversation ran to completion. The <c>Result</c> on the run record carries
    /// the conversation outcome (which may itself report a failed turn); the token totals are populated.
    /// </summary>
    Succeeded = 2,

    /// <summary>
    /// Terminal — the run could not complete: an unhandled exception escaped the conversation, the staged
    /// bundle backing the handle was gone when the dispatcher tried to run it, or the run was cancelled by
    /// host shutdown. The <c>Error</c> on the run record carries a stable, caller-safe reason.
    /// </summary>
    Failed = 3
}
