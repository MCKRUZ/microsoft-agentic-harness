namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Counts the workflow and evaluation run records still held for a person, so an erasure receipt can say when one was
/// kept (#926).
/// </summary>
/// <remarks>
/// <para>
/// The run store erases a person's finished runs but keeps every run still queued, running or waiting for an approval:
/// removing a running run's record would unlock its target while the dispatcher is still executing it, and removing a
/// parked one would strand the approvals it waits on. An eraser can only report how many records it removed, so the
/// erasure coordinator asks this once every stage has run and, when any record is still held, lists it on the receipt and
/// tells the person to run erasure again, as it does for a planned task still running.
/// </para>
/// <para>
/// <strong>Every record still held, not only the live ones.</strong> A run that was live when the store was erased and
/// ended before this is asked is no longer live, but its record is still held until its retention runs out; counting
/// only live runs would let the receipt read <c>Full</c> while it remained.
/// </para>
/// <para>
/// Part of <c>IRunJobStore</c>, so a store that replaces the shipped one has to answer it too. The bundle API's run and
/// handle stores answer it the same way (#990): each keeps what a run still in flight is using, and each gets a receipt
/// line of its own, named by <see cref="KeptRunsStoreName"/>, so the person is told which kind of record was kept.
/// </para>
/// </remarks>
public interface IKeptRunSource
{
    /// <summary>The receipt line's store name when a record was kept, such as <c>"bundle runs still held"</c>.</summary>
    string KeptRunsStoreName { get; }

    /// <summary>The receipt line's reason: what was kept, why, and that the person should run erasure again.</summary>
    string KeptRunsReason { get; }

    /// <summary>Counts every run record held for <paramref name="ownerId"/>, in every tenant, whatever its status.</summary>
    /// <param name="ownerId">The person's user id, in either the issued or the canonical form.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many of the person's run records the store still holds.</returns>
    Task<int> CountRunsKeptAsync(string ownerId, CancellationToken cancellationToken);
}
