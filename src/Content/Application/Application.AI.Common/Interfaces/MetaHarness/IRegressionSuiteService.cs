using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Manages the self-maintained regression suite for the meta-harness optimization loop.
/// </summary>
/// <remarks>
/// The regression suite is a set of eval task IDs that must continue passing before a new
/// candidate can be accepted as the best. It is built up automatically over time: each time
/// a candidate is accepted as best, previously-failing tasks that now pass are promoted into
/// the suite. This prevents accepting candidates that improve overall but regress on
/// previously-solved tasks.
/// </remarks>
public interface IRegressionSuiteService
{
    /// <summary>
    /// Loads the regression suite from <c>regression_suite.json</c> in the run directory.
    /// Returns an empty suite (always-pass) when the file does not exist or cannot be parsed.
    /// </summary>
    /// <param name="runDirectoryPath">Absolute path to the <c>optimizations/{runId}/</c> directory.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RegressionSuite> LoadAsync(string runDirectoryPath, CancellationToken ct = default);

    /// <summary>
    /// Checks whether <paramref name="evalResult"/> passes the regression gate.
    /// </summary>
    /// <remarks>
    /// Only task IDs present in both the suite and the eval results are considered.
    /// Tasks in the suite but absent from the eval results are treated as failed (conservative).
    /// Always returns <c>Passed = true</c> when the suite is empty.
    /// </remarks>
    /// <param name="suite">The current regression suite to check against.</param>
    /// <param name="evalResult">Evaluation results from the candidate under consideration.</param>
    RegressionCheckResult Check(RegressionSuite suite, EvaluationResult evalResult);

    /// <summary>
    /// Promotes newly-fixed tasks into the regression suite and persists the updated suite.
    /// </summary>
    /// <remarks>
    /// A task is "newly fixed" when it failed in <paramref name="previousBestResults"/> but passed
    /// in <paramref name="currentResults"/>. When <paramref name="previousBestResults"/> is <c>null</c>
    /// (first winning iteration), all currently-passing tasks are promoted to seed the suite.
    /// Returns the suite unchanged when no new tasks qualify for promotion.
    /// </remarks>
    /// <param name="suite">The current regression suite to extend.</param>
    /// <param name="currentResults">Evaluation results of the newly accepted best candidate.</param>
    /// <param name="previousBestResults">Evaluation results of the prior best candidate, or <c>null</c> on first win.</param>
    /// <param name="runDirectoryPath">Absolute path to the <c>optimizations/{runId}/</c> directory.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RegressionSuite> PromoteAsync(
        RegressionSuite suite,
        EvaluationResult currentResults,
        EvaluationResult? previousBestResults,
        string runDirectoryPath,
        CancellationToken ct = default);
}
