namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Stops a person's workflow and evaluation runs that have not ended when their erasure starts (#992).
/// </summary>
/// <remarks>
/// <para>
/// The run store erases only finished runs, because removing a live run's record would unlock its workflow mid-run or
/// strand the approvals it waits on. Left alone, a queued run would start after the person was erased and write new
/// personal data, and a parked one would keep every erasure retry <c>Partial</c> until the host's parked-run ceiling
/// failed it, or for ever when that ceiling is switched off. Stopping them first, through the same cancel path an owner
/// uses, ends them and withdraws their approvals, so the run store's eraser then removes them with the rest.
/// </para>
/// <para>
/// Only the person's own runs are stopped: they are selected by owner, as erasure selects everything else. A run that
/// does not stop in time is handled as before: its record is kept and the receipt says to run erasure again.
/// </para>
/// </remarks>
public interface IOwnedRunCanceller
{
    /// <summary>
    /// Cancels every queued or parked run <paramref name="ownerId"/> owns, signals every executing one that can be
    /// signalled, then waits a bounded time for the executing ones to end.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <param name="cancellationToken">Cancels the wait, not the cancellations already made.</param>
    /// <returns>How many of the person's runs had still not ended when the wait ended.</returns>
    Task<int> CancelRunsAsync(string ownerId, CancellationToken cancellationToken);
}
