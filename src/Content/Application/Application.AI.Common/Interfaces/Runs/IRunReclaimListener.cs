namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Releases whatever it holds against a run whose record has been reclaimed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A run's identifier keys more than its record.</strong> Progress bookkeeping is held against
/// it, and so is whatever a given <see cref="Domain.AI.Runs.RunKind"/> needs beyond the kind-agnostic
/// record — an evaluation's dataset names and its report, for instance. Each of those is a side table
/// that grows for the life of the process unless something drops its entry when the run goes.
/// </para>
/// <para>
/// <strong>Why a seam rather than a dependency per table.</strong> The sweeper is the one place that
/// knows a run has been reclaimed. Naming each holder there means every future kind of run adds a
/// constructor parameter to a service that has nothing to do with it — and the kind whose author
/// forgets is not a compile error, it is an unbounded leak nobody observes until the host runs out of
/// memory. Listening instead makes releasing a property of holding.
/// </para>
/// <para>
/// <strong>Implementations must not throw.</strong> The sweep is a loop over every listener; one that
/// fails would stop the rest from being told, so a run's record would be gone while another holder
/// still had its entry — the exact leak this exists to close. Log and carry on.
/// </para>
/// </remarks>
public interface IRunReclaimListener
{
    /// <summary>
    /// Drops everything held for the given runs. Identifiers that were never held are ignored.
    /// </summary>
    /// <remarks>
    /// Takes the whole batch rather than one identifier at a time so an implementation backed by
    /// something with a per-call cost — a round trip, a lock, a transaction — can pay it once.
    /// </remarks>
    /// <param name="jobIds">The runs whose records have been reclaimed.</param>
    void OnRunsReclaimed(IReadOnlyList<string> jobIds);
}
