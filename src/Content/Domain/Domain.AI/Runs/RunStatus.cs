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
    /// Terminal for the run, because the dispatcher is done with it; acting on the decision starts a
    /// new run rather than reviving this one.
    /// </summary>
    Blocked = 5
}
