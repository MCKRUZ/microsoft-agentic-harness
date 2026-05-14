using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Provides eval tasks for a given candidate. Implementations resolve tasks from
/// a configured source (file system, database, or in-memory registry).
/// When not registered in DI, the evaluation workflow falls back to an empty task list
/// and relies on <see cref="IEvaluationService"/> to use its internal task source.
/// </summary>
public interface IEvalTaskSource
{
    /// <summary>
    /// Loads evaluation tasks applicable to the given candidate.
    /// </summary>
    /// <param name="candidate">The candidate whose configuration determines which tasks apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The eval tasks to run against the candidate.</returns>
    Task<IReadOnlyList<EvalTask>> GetTasksAsync(
        HarnessCandidate candidate,
        CancellationToken cancellationToken = default);
}
