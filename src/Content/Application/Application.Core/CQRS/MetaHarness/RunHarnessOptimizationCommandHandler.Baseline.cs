using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
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
    /// <param name="EvaluatedSeed">The seed with its score (measured now, or before a restart), or null when a restored best made it unnecessary.</param>
    /// <param name="ToBeatEvaluation">The per-task results of <paramref name="ToBeat"/>, when this invocation measured them.</param>
    /// <param name="RegressionSuite">The regression suite, seeded from the seed's passing tasks on a fresh baseline.</param>
    private sealed record RunBaseline(
        HarnessCandidate ToBeat,
        HarnessCandidate? RestoredBest,
        HarnessCandidate? EvaluatedSeed,
        EvaluationResult? ToBeatEvaluation,
        RegressionSuite RegressionSuite);

    // A resumed run reuses the seed it recorded, so it neither builds a second seed nor measures one again (#1077). The
    // recorded seed may be a candidate imported from another run by the first invocation's SeedCandidateId; it is still
    // this run's seed, though its stored score is not this run's bar (see EstablishBaselineAsync). An explicit
    // SeedCandidateId always wins: it names an existing record, which is never rebuilt or re-saved.
    private async Task<(HarnessCandidate Seed, bool Restored)> ResolveRunSeedAsync(
        RunHarnessOptimizationCommand command, MetaHarnessConfig cfg, Guid? recordedSeedId, CancellationToken ct)
    {
        if (command.SeedCandidateId is null && recordedSeedId is Guid id)
        {
            var recorded = await _candidateRepository.GetAsync(id, ct);
            if (recorded is not null)
                return (recorded, true);

            _logger.LogWarning(
                "Optimization run {RunId} recorded seed candidate {CandidateId}, but it is missing; building a new " +
                "seed instead", command.OptimizationRunId, id);
        }

        return (await ResolveSeedCandidateAsync(command, cfg, ct), false);
    }

    // The next proposal is built from the latest evaluated candidate, exactly as the loop does between iterations. A
    // record that is missing, unevaluated or from another run is no starting point, so the caller falls back to the seed.
    private async Task<HarnessCandidate?> RestoreWorkingCandidateAsync(
        Guid? recordedWorkingId, Guid optimizationRunId, CancellationToken ct)
    {
        if (recordedWorkingId is not Guid id)
            return null;

        var recorded = await _candidateRepository.GetAsync(id, ct);
        if (recorded is { Status: HarnessCandidateStatus.Evaluated } && recorded.OptimizationRunId == optimizationRunId)
            return recorded;

        _logger.LogWarning(
            "Optimization run {RunId} recorded working candidate {CandidateId}, but it is missing, unevaluated or " +
            "from another run; building the next proposal from the seed instead", optimizationRunId, id);
        return null;
    }

    // Iterations since the last accepted proposal. Every iteration that is not accepted adds one, and the recorded best
    // is the last one accepted, so the count before a restart is derived rather than stored. When the manifest names a
    // best that cannot be restored, when the last improvement happened is unknown: the count starts again at zero rather
    // than treating every completed iteration as a failure, which would silently stop the run.
    private static int NoImprovementCountSoFar(
        int lastCompletedIteration, Guid? recordedBestId, HarnessCandidate? restoredBest) =>
        recordedBestId is not null && restoredBest is null
            ? 0
            : lastCompletedIteration - (restoredBest?.Iteration ?? 0);

    // The proposer's view of the attempts already made in this run, rebuilt after a restart. Only iterations the
    // manifest recorded as completed count: a candidate saved by an iteration interrupted before its manifest write is
    // about to be attempted again, so it is not history.
    private async Task<List<Guid>> ListPriorCandidateIdsAsync(
        Guid optimizationRunId, int lastCompletedIteration, CancellationToken ct) =>
        (await _candidateRepository.ListAsync(optimizationRunId, ct))
            .Where(c => c.Iteration > 0 && c.Iteration <= lastCompletedIteration
                && c.Status is HarnessCandidateStatus.Evaluated or HarnessCandidateStatus.Failed)
            .OrderBy(c => c.Iteration)
            .Select(c => c.CandidateId)
            .ToList();

    private async Task<RunBaseline> EstablishBaselineAsync(
        RunHarnessOptimizationCommand command,
        HarnessCandidate? restored,
        HarnessCandidate seed,
        bool seedRestored,
        IReadOnlyList<EvalTask> evalTasks,
        RegressionSuite suite,
        string runDir,
        CancellationToken ct)
    {
        if (restored is not null)
            return new RunBaseline(restored, restored, EvaluatedSeed: null, ToBeatEvaluation: null, suite);

        // The run's own seed was measured before the restart: its score is the bar, and the regression suite was seeded
        // from it then, so neither is redone (#1077). A seed imported from another run keeps that run's score on its
        // record, measured against that run's eval tasks, so it is measured again below.
        if (seedRestored && seed is { Status: HarnessCandidateStatus.Evaluated, BestScore: not null }
            && seed.OptimizationRunId == command.OptimizationRunId)
        {
            var measuredSeed = seed with { Iteration = 0 };
            _logger.LogInformation(
                "Optimization run {RunId} resumed with seed {CandidateId} as the baseline (pass rate: {PassRate:P2})",
                command.OptimizationRunId, seed.CandidateId, seed.BestScore);
            return new RunBaseline(measuredSeed, RestoredBest: null, measuredSeed, ToBeatEvaluation: null, suite);
        }

        var (evaluatedSeed, seedResult) = await EvaluateSeedAsync(command.OptimizationRunId, seed, evalTasks, ct);

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
        Guid optimizationRunId, HarnessCandidate seed, IReadOnlyList<EvalTask> evalTasks, CancellationToken ct)
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

        // Only the run's own seed is saved: built by this run, with no parent. Any other record (an earlier proposal of
        // this run, or a candidate from another run) keeps its own iteration and score, so it is never re-saved here.
        if (seed.OptimizationRunId == optimizationRunId && seed.ParentCandidateId is null)
            await _candidateRepository.SaveAsync(evaluated, ct);

        _logger.LogInformation(
            "Optimization run {RunId} baseline: seed {CandidateId} pass rate {PassRate:P2}",
            optimizationRunId, seed.CandidateId, result.PassRate);
        return (evaluated, result);
    }
}
