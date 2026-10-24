namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Stops a person's planned tasks that are running when their erasure starts (#907).
/// </summary>
/// <remarks>
/// <para>
/// Erasure reads the ids a person's planned-task runs record data under, clears the stores keyed by those ids, and then
/// deletes the plans. A run already in progress keeps writing under the same ids after those stores are cleared, so its
/// records outlive the erasure; and the plan store keeps a plan with a run in progress, so the receipt has to ask for
/// erasure to run again. Stopping the person's runs first, and waiting briefly for them to end, closes most of that.
/// </para>
/// <para>
/// Only runs of plans the person owns are stopped: only a plan's owner can run it, so this never stops another person's
/// task. A run that does not stop in time is handled as before: its plan is kept and the receipt says to run erasure
/// again.
/// </para>
/// </remarks>
public interface IPlannedTaskCanceller
{
    /// <summary>
    /// Signals every run in progress of a plan <paramref name="ownerId"/> owns, then waits a bounded time for them to end.
    /// </summary>
    /// <param name="ownerId">The person, as their identity provider issued the id.</param>
    /// <param name="cancellationToken">Cancels the wait, not the signals already sent.</param>
    /// <returns>How many of the person's runs were still in progress when the wait ended.</returns>
    Task<int> StopRunningTasksAsync(string ownerId, CancellationToken cancellationToken);
}
