namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Counts a person's workflow and evaluation runs that have not ended, so an erasure receipt can say when one was kept
/// (#926).
/// </summary>
/// <remarks>
/// <para>
/// The run store erases a person's finished runs but keeps every run still queued, running or waiting for an approval:
/// removing a running run's record would unlock its target while the dispatcher is still executing it, and removing a
/// parked one would strand the approvals it waits on. An eraser can only report how many records it removed, so the
/// erasure coordinator asks this afterwards and, when any run is still live, lists it on the receipt and tells the person
/// to run erasure again, as it does for a planned task still running.
/// </para>
/// <para>
/// Implemented by the run store itself and forwarded from <c>IRunJobStore</c>. A host whose store does not implement it
/// is simply not asked.
/// </para>
/// </remarks>
public interface ILiveRunSource
{
    /// <summary>Counts the runs <paramref name="ownerId"/> owns, in every tenant, that are queued, running or parked.</summary>
    /// <param name="ownerId">The person's user id, in either the issued or the canonical form.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many of the person's runs have not ended.</returns>
    Task<int> CountLiveRunsAsync(string ownerId, CancellationToken cancellationToken);
}
