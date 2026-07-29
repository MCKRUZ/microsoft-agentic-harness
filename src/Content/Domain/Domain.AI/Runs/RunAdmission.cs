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
    /// The owner already holds as many live runs as the host permits.
    /// </summary>
    /// <remarks>
    /// Bounds how much work one caller may have <em>accepted</em> — queued or executing — which
    /// neither the per-request rate limit nor any per-workflow ceiling expresses: a caller within both
    /// can otherwise start every workflow it owns at once. It is not a statement about how much runs
    /// concurrently; that is the host's dispatch degree, and it is not a fairness mechanism either.
    /// </remarks>
    OwnerAtCapacity = 2
}
