namespace Domain.AI.Runs;

/// <summary>
/// Lifecycle of a queued run.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Cancelled"/> is distinct from <see cref="Failed"/> deliberately. Both are terminal, but
/// one says the work was stopped on purpose and the other says it broke — an operator triaging a
/// queue needs to tell those apart, and collapsing them makes every deliberate stop look like an
/// incident.
/// </para>
/// <para>
/// <see cref="Blocked"/> is the same argument taken one step further. Work that parked awaiting a
/// human decision neither succeeded nor broke, and reporting it as either misleads: as
/// <see cref="Succeeded"/> it claims an outcome that was never produced, and as <see cref="Failed"/>
/// it sends an operator hunting for a fault when what is actually needed is an approval.
/// </para>
/// </remarks>
public enum RunStatus
{
    /// <summary>Accepted and waiting for a dispatcher to claim it.</summary>
    Queued = 0,

    /// <summary>Claimed by a dispatcher and executing.</summary>
    Running = 1,

    /// <summary>Finished successfully.</summary>
    Succeeded = 2,

    /// <summary>Finished unsuccessfully. <see cref="RunRecord.Error"/> carries a caller-safe reason.</summary>
    Failed = 3,

    /// <summary>Stopped on request before it could finish.</summary>
    Cancelled = 4,

    /// <summary>
    /// Parked awaiting a decision this run cannot make for itself — typically a human approval gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Live, not terminal.</strong> The dispatcher is idle on this run, but the work is not
    /// over: answering the gate continues <em>this</em> run under the same job id. That is what a
    /// caller means by "my workflow" — one identifier for one unit of work, whatever it had to wait
    /// for along the way — and it is why <see cref="RunRecord.IsTerminal"/> lists this among the live
    /// states.
    /// </para>
    /// <para>
    /// Reading it as terminal has a sharper consequence than an untidy status. Admission permits one
    /// live run per target, so a parked run reported as finished <em>releases its workflow</em>: a
    /// second run starts against the same plan state machine and can answer the first run's gate. A
    /// parked run therefore keeps its slot, and keeps the workflow locked, until the gate is resolved.
    /// </para>
    /// <para>
    /// The cost of being live is that retention only reclaims terminal runs, so a gate nobody ever
    /// answers would hold a slot for the host's life. <c>MaxParkedRunDuration</c> bounds it: a run
    /// parked longer than that is failed with a caller-safe reason, which makes it terminal and
    /// reclaimable again.
    /// </para>
    /// </remarks>
    Blocked = 5
}
