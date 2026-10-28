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
/// <strong>Two calls, either side of stopping the person's planned tasks.</strong> <see cref="CancelPendingRunsAsync"/>
/// comes first, so a queued workflow run cannot start a planned task after <see cref="IPlannedTaskCanceller"/> has looked
/// for them. An executing workflow is not signalled here: the planned-task canceller signals it, filtered on the plan's
/// owner, because the signal is by plan and would stop anyone's run of it. <see cref="WaitForRunsAsync"/> comes after,
/// and waits a bounded time for what is still executing, cancelling anything that went back to waiting meanwhile.
/// </para>
/// <para>
/// Only the person's own runs are touched: they are selected by owner, as erasure selects everything else. A run that
/// does not end in time is handled as before: its record is kept and the receipt says to run erasure again.
/// </para>
/// </remarks>
public interface IOwnedRunCanceller
{
    /// <summary>Cancels every queued or parked run <paramref name="ownerId"/> owns, withdrawing its approvals.</summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <returns>How many runs were cancelled.</returns>
    Task<int> CancelPendingRunsAsync(string ownerId);

    /// <summary>
    /// Waits a bounded time for the runs <paramref name="ownerId"/> has executing to end, cancelling on every poll any of
    /// their runs that is queued or parked, including one queued or parked since <see cref="CancelPendingRunsAsync"/>.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <param name="cancellationToken">Cancels the wait, not the cancellations already made.</param>
    /// <returns>How many of the person's runs had still not ended when the wait ended.</returns>
    Task<int> WaitForRunsAsync(string ownerId, CancellationToken cancellationToken);
}
