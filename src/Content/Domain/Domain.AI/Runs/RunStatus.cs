namespace Domain.AI.Runs;

/// <summary>
/// Lifecycle of a queued run.
/// </summary>
/// <remarks>
/// <see cref="Cancelled"/> is distinct from <see cref="Failed"/> deliberately. Both are terminal, but
/// one says the work was stopped on purpose and the other says it broke — an operator triaging a
/// queue needs to tell those apart, and collapsing them makes every deliberate stop look like an
/// incident.
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
    Cancelled = 4
}
