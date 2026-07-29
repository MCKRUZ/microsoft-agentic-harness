namespace Domain.AI.Runs;

/// <summary>
/// Outcome of offering a run to the store: admitted, or refused by a named limit.
/// </summary>
/// <remarks>
/// Named rather than boolean because the two refusals mean opposite things to a caller. Being at
/// capacity is about the caller and clears on its own as its own work finishes; a target already
/// running is about that one workflow and clears only when that run ends. A caller told merely "no"
/// cannot tell whether to back off or to stop asking.
/// </remarks>
public enum RunAdmission
{
    /// <summary>The run was stored and may be queued.</summary>
    Accepted = 0,

    /// <summary>
    /// The target already has a live run. A stored workflow's execution state is singular — it is
    /// keyed by the workflow, not by the run — so a second concurrent run would share one state
    /// machine with the first: re-executing steps the first has in flight and adopting its outputs.
    /// </summary>
    TargetAlreadyRunning = 1,

    /// <summary>
    /// The owner already holds as many live runs as the host permits. Bounds one caller's claim on
    /// the host at any instant, which neither the per-request rate limit nor any per-workflow ceiling
    /// expresses: a caller within both can otherwise start every workflow it owns at once, and each
    /// one then multiplies by its own parallel-step allowance.
    /// </summary>
    OwnerAtCapacity = 2
}
