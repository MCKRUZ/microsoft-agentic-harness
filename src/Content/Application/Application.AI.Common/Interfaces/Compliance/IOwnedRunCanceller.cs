namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Ends a person's workflow and evaluation runs that have not ended when their erasure starts (#992).
/// </summary>
/// <remarks>
/// <para>
/// The run store erases only finished runs, because removing a live run's record would unlock its workflow mid-run or
/// strand the approvals it waits on. Left alone, a queued run would start after the person was erased and write new
/// personal data, and a parked one would keep every erasure retry <c>Partial</c> until the host's parked-run ceiling
/// failed it, or for ever when that ceiling is switched off. Cancelling them first, through the same cancel path an owner
/// uses, ends them and withdraws their approvals, so the run store's eraser then removes them with the rest.
/// </para>
/// <para>
/// <strong>Two calls.</strong> <see cref="CancelRunsAsync"/> comes before the person's planned tasks are stopped, so a
/// queued workflow run cannot start a planned task after <see cref="IPlannedTaskCanceller"/> has looked for them.
/// <see cref="WaitForRunsAsync"/> runs alongside that stop, so the two bounded waits overlap rather than add up, and on
/// every poll it cancels what is queued or parked and signals what is executing again, which reaches a run claimed after
/// the planned-task canceller looked.
/// </para>
/// <para>
/// Only the person's own runs are touched: they are selected by owner, as erasure selects everything else. A run that
/// does not end in time is handled as before: its record is kept and the receipt says to run erasure again.
/// </para>
/// </remarks>
public interface IOwnedRunCanceller
{
    /// <summary>
    /// Cancels every queued or parked run <paramref name="ownerId"/> owns, withdrawing its approvals, and signals every
    /// executing workflow of theirs to stop.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <returns>How many runs were cancelled.</returns>
    Task<int> CancelRunsAsync(string ownerId);

    /// <summary>
    /// Waits a bounded time until no run <paramref name="ownerId"/> owns is live, repeating <see cref="CancelRunsAsync"/>
    /// on every poll for whatever of theirs is live then, so a run executing, newly parked or newly queued is still stopped.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <param name="cancellationToken">Cancels the wait, not the cancellations already made.</param>
    /// <returns>How many of the person's runs had still not ended when the wait ended.</returns>
    Task<int> WaitForRunsAsync(string ownerId, CancellationToken cancellationToken);
}
