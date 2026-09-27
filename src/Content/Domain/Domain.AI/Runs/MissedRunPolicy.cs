namespace Domain.AI.Runs;

/// <summary>
/// What a schedule does when the host was not running at one or more of its scheduled ticks.
/// </summary>
public enum MissedRunPolicy
{
    /// <summary>
    /// Drops whatever was missed and resumes from the current time. The schedule's next fire time
    /// is recomputed from "now" — it never looks backward.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Fires exactly one run for the entire missed backlog, then resumes from the current time.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>one</em> run, never one per missed interval. An outage spanning many ticks
    /// (a laptop closed overnight, a long deploy) must not enqueue a run for every tick it missed —
    /// that turns a restart into a burst of duplicate work against the same target.
    /// </remarks>
    CatchUpOnce = 1
}
