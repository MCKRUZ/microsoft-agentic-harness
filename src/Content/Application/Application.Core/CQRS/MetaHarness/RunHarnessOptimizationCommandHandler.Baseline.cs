using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.MetaHarness;

public sealed partial class RunHarnessOptimizationCommandHandler
{
    /// <summary>
    /// What a run compares its first proposal against, so no proposal is ever accepted against nothing.
    /// </summary>
    /// <param name="ToBeat">The measured candidate the next proposal must beat: the restored best, or the evaluated seed.</param>
    /// <param name="RestoredBest">The best a resumed run had already accepted, or null on a fresh run.</param>
    /// <param name="EvaluatedSeed">The seed with its score, or null when a restored best made measuring it unnecessary.</param>
    /// <param name="ToBeatEvaluation">The per-task results of <paramref name="ToBeat"/>, when this invocation measured them.</param>
    /// <param name="RegressionSuite">The regression suite, seeded from the seed's passing tasks on a fresh baseline.</param>
    private sealed record RunBaseline(
        HarnessCandidate ToBeat,
        HarnessCandidate? RestoredBest,
        HarnessCandidate? EvaluatedSeed,
        EvaluationResult? ToBeatEvaluation,
        RegressionSuite RegressionSuite);

    private async Task<RunBaseline> EstablishBaselineAsync(
        RunHarnessOptimizationCommand command,
        Guid? recordedBestId,
        HarnessCandidate seed,
        IReadOnlyList<EvalTask> evalTasks,
        RegressionSuite suite,
        string runDir,
        CancellationToken ct)
    {
        var restored = await RestoreRecordedBestAsync(recordedBestId, command.OptimizationRunId, ct);
        if (restored is not null)
            return new RunBaseline(restored, restored, EvaluatedSeed: null, ToBeatEvaluation: null, suite);

        var (evaluatedSeed, seedResult) = await EvaluateSeedAsync(
            command.OptimizationRunId, seed, seedBuiltByThisRun: command.SeedCandidateId is null, evalTasks, ct);

        // The seed is the first configuration the run stands behind, so the tasks it already solves seed the regression
        // suite: a later proposal cannot win by breaking them.
        var seededSuite = await _regressionService.PromoteAsync(
            suite, seedResult, previousBestResults: null, runDir, ct);
        return new RunBaseline(evaluatedSeed, RestoredBest: null, evaluatedSeed, seedResult, seededSuite);
    }

    // A resumed run continues from the best it had already accepted. A record that is missing, belongs to another run,
    // or was never scored is no baseline, so the seed is measured instead.
    private async Task<HarnessCandidate?> RestoreRecordedBestAsync(
        Guid? recordedBestId, Guid optimizationRunId, CancellationToken ct)
    {
        if (recordedBestId is not Guid id)
            return null;

        var recorded = await _candidateRepository.GetAsync(id, ct);
        if (recorded is { Status: HarnessCandidateStatus.Evaluated, BestScore: not null }
            && recorded.OptimizationRunId == optimizationRunId)
        {
            _logger.LogInformation(
                "Optimization run {RunId} resumed with best candidate {CandidateId} (pass rate: {PassRate:P2})",
                optimizationRunId, id, recorded.BestScore);
            return recorded;
        }

        _logger.LogWarning(
            "Optimization run {RunId} recorded best candidate {CandidateId}, but it is missing, unscored or from " +
            "another run; measuring the seed as the baseline instead",
            optimizationRunId, id);
        return null;
    }

    private async Task<(HarnessCandidate Seed, EvaluationResult Result)> EvaluateSeedAsync(
        Guid optimizationRunId, HarnessCandidate seed, bool seedBuiltByThisRun, IReadOnlyList<EvalTask> evalTasks,
        CancellationToken ct)
    {
        EvaluationResult result;
        try
        {
            result = await _evaluationService.EvaluateAsync(seed, evalTasks, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Optimization run {optimizationRunId} could not evaluate its seed candidate {seed.CandidateId}, so it " +
                "has no baseline to judge proposals against. Fix the evaluation failure and run again.", ex);
        }

        // Iteration 0 within this run, so an equally good proposal never displaces the unchanged seed on the
        // earlier-iteration tie-break, including for a seed imported from another run.
        var evaluated = seed with
        {
            Iteration = 0,
            BestScore = result.PassRate,
            TokenCost = result.TotalTokenCost,
            Status = HarnessCandidateStatus.Evaluated,
        };

        // Only a seed this invocation built is saved. A seed named by SeedCandidateId is an existing record, from this
        // run or another, and re-saving it as iteration 0 would rewrite the proposal it was.
        if (seedBuiltByThisRun)
            await _candidateRepository.SaveAsync(evaluated, ct);

        _logger.LogInformation(
            "Optimization run {RunId} baseline: seed {CandidateId} pass rate {PassRate:P2}",
            optimizationRunId, seed.CandidateId, result.PassRate);
        return (evaluated, result);
    }
}
