using System.Diagnostics;
using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Evaluation.Runners;

/// <summary>
/// The default <see cref="IEvalRunner"/> implementation. Runs sequentially when
/// <see cref="EvalRunOptions.Parallelism"/> is 1; otherwise bounds concurrency
/// with a <see cref="SemaphoreSlim"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per case the runner repeats invocation <see cref="EvalRunOptions.Repeats"/> times
/// (one or more) and aggregates per-metric scores via median to smooth out
/// non-deterministic LLM-judge noise. The case's overall verdict is the worst
/// per-metric aggregated verdict (Fail &gt; Warn &gt; Pass).
/// </para>
/// <para>
/// Exceptions thrown by the invoker are captured per-case as
/// <see cref="EvalResult.Error"/> and counted toward <see cref="EvalRunReport.ErroredCount"/>;
/// they do not abort the run.
/// </para>
/// </remarks>
public sealed class EvalRunner : IEvalRunner
{
    private readonly IAgentInvoker _invoker;
    private readonly IReadOnlyDictionary<string, IEvalMetric> _metricsByKey;
    private readonly ILogger<EvalRunner> _logger;

    /// <summary>Initializes the runner.</summary>
    public EvalRunner(
        IAgentInvoker invoker,
        IEnumerable<IEvalMetric> metrics,
        ILogger<EvalRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _invoker = invoker;
        _logger = logger;
        _metricsByKey = metrics.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<EvalRunReport> RunAsync(
        IReadOnlyList<EvalDataset> datasets,
        EvalRunOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(options);

        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var allCases = datasets
            .SelectMany(ds => ds.Cases.Select(c => (Dataset: ds, Case: c)))
            .Where(pair => MatchesTagFilter(pair.Case, options.TagFilter))
            .ToList();

        var warnings = ValidateRecognizedKeys(allCases.Select(pair => pair.Case).ToList(), options);

        _logger.LogInformation(
            "Eval run {RunId} starting — {DatasetCount} dataset(s), {CaseCount} case(s) after filter, repeats={Repeats}, parallelism={Parallelism}",
            runId, datasets.Count, allCases.Count, options.Repeats, options.Parallelism);

        var results = options.Parallelism <= 1
            ? await RunSequentialAsync(allCases, options, cancellationToken)
            : await RunParallelAsync(allCases, options, cancellationToken);

        sw.Stop();
        var passed = results.Count(r => r.Verdict == Verdict.Pass);
        var failed = results.Count(r => r.Verdict == Verdict.Fail);
        var warned = results.Count(r => r.Verdict == Verdict.Warn);
        var errored = results.Count(r => r.Error is not null);
        var totalCost = results.Sum(r => r.CostUsd);
        var scoredCount = passed + failed + warned;
        var failRate = scoredCount == 0 ? 0.0 : (double)failed / scoredCount;
        var overallVerdict = errored > 0 || failRate > options.FailRateThreshold
            ? Verdict.Fail
            : warned > 0 ? Verdict.Warn : Verdict.Pass;

        _logger.LogInformation(
            "Eval run {RunId} complete — {Passed} passed, {Failed} failed, {Warned} warned, {Errored} errored, cost ${Cost:F4}, verdict {Verdict}",
            runId, passed, failed, warned, errored, totalCost, overallVerdict);

        return new EvalRunReport
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt + sw.Elapsed,
            Duration = sw.Elapsed,
            Datasets = datasets,
            Results = results,
            PassedCount = passed,
            FailedCount = failed,
            WarnedCount = warned,
            ErroredCount = errored,
            TotalCostUsd = totalCost,
            Repeats = options.Repeats,
            OverallVerdict = overallVerdict,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Compares every case's declared <see cref="MetricSpec.Parameters"/> and
    /// <see cref="EvalCase.InvocationOverrides"/> keys against the resolved metric's/invoker's own
    /// declared <see cref="IEvalMetric.RecognizedParameterKeys"/>/<see cref="IAgentInvoker.RecognizedOverrideKeys"/>,
    /// surfacing anything unrecognized as a report-level warning.
    /// </summary>
    /// <remarks>
    /// #437: <c>MetricSpecParameterKeyValidationTests</c> (#423) runs this same comparison at build
    /// time, but only against the repo's own checked-in <c>eval-datasets/**</c> corpus — a template
    /// consumer's own dataset, loaded and run through <see cref="RunAsync"/> at runtime, got none of
    /// that protection. This closes the gap for every dataset a real run actually processes, not just
    /// the ones checked into this repo. Deliberately fail-soft, matching
    /// <see cref="IEvalMetric.RecognizedParameterKeys"/>'s own documented contract: a typo'd key still
    /// scores (falling back to whatever default the accessor picks), it just no longer does so
    /// silently.
    /// </remarks>
    private List<string> ValidateRecognizedKeys(IReadOnlyList<EvalCase> cases, EvalRunOptions options)
    {
        var warnings = new List<string>();

        var unrecognizedRunLevelOverrides = (options.InvocationOverrides?.Keys ?? [])
            .Where(k => !_invoker.RecognizedOverrideKeys.Contains(k))
            .ToList();
        if (unrecognizedRunLevelOverrides.Count > 0)
        {
            var warning =
                $"Run-level invocation override key(s) {string.Join(", ", unrecognizedRunLevelOverrides.Select(k => $"'{k}'"))} " +
                $"not recognized by {_invoker.GetType().Name} (recognized: {string.Join(", ", _invoker.RecognizedOverrideKeys)}).";
            warnings.Add(warning);
            _logger.LogWarning("{Warning}", warning);
        }

        foreach (var @case in cases)
        {
            var unrecognizedOverrides = @case.InvocationOverrides.Keys
                .Where(k => !_invoker.RecognizedOverrideKeys.Contains(k))
                .ToList();
            if (unrecognizedOverrides.Count > 0)
            {
                var warning =
                    $"Case '{@case.Id}': invocation override key(s) {string.Join(", ", unrecognizedOverrides.Select(k => $"'{k}'"))} " +
                    $"not recognized by {_invoker.GetType().Name} (recognized: {string.Join(", ", _invoker.RecognizedOverrideKeys)}).";
                warnings.Add(warning);
                _logger.LogWarning("{Warning}", warning);
            }

            foreach (var spec in @case.MetricSpecs)
            {
                if (!_metricsByKey.TryGetValue(spec.MetricKey, out var metric))
                    continue; // Reported separately when the case is actually scored.

                var unrecognizedParams = spec.Parameters.Keys
                    .Where(k => !metric.RecognizedParameterKeys.Contains(k))
                    .ToList();
                if (unrecognizedParams.Count == 0)
                    continue;

                var warning =
                    $"Case '{@case.Id}', metric '{spec.MetricKey}': parameter key(s) " +
                    $"{string.Join(", ", unrecognizedParams.Select(k => $"'{k}'"))} not recognized " +
                    $"(recognized: {string.Join(", ", metric.RecognizedParameterKeys)}).";
                warnings.Add(warning);
                _logger.LogWarning("{Warning}", warning);
            }
        }

        return warnings;
    }

    private async Task<IReadOnlyList<EvalResult>> RunSequentialAsync(
        IReadOnlyList<(EvalDataset Dataset, EvalCase Case)> cases,
        EvalRunOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<EvalResult>(cases.Count);
        foreach (var (_, @case) in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScoreCaseAsync(@case, options, cancellationToken));
        }
        return results;
    }

    private async Task<IReadOnlyList<EvalResult>> RunParallelAsync(
        IReadOnlyList<(EvalDataset Dataset, EvalCase Case)> cases,
        EvalRunOptions options,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(options.Parallelism);
        var tasks = cases.Select(async pair =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await ScoreCaseAsync(pair.Case, options, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<EvalResult> ScoreCaseAsync(
        EvalCase @case,
        EvalRunOptions options,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var outputs = new List<string>(options.Repeats);
        var scoresPerRepeat = new List<IReadOnlyList<MetricScore>>(options.Repeats);
        decimal totalCost = 0m;
        string? caseError = null;

        for (var i = 0; i < options.Repeats; i++)
        {
            AgentInvocationResult invocation;
            try
            {
                invocation = await _invoker.InvokeAsync(
                    @case,
                    options.InvocationOverrides,
                    options.ForceDeterministic,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Case {CaseId} repeat {Repeat} failed to invoke harness", @case.Id, i);
                caseError = ex.Message;
                break;
            }

            outputs.Add(invocation.Output);
            totalCost += invocation.CostUsd;

            var repeatScores = new List<MetricScore>(@case.MetricSpecs.Count);
            foreach (var spec in @case.MetricSpecs)
            {
                MetricScore score;
                if (!_metricsByKey.TryGetValue(spec.MetricKey, out var metric))
                {
                    score = new MetricScore
                    {
                        MetricKey = spec.MetricKey,
                        Score = 0.0,
                        Verdict = Verdict.Warn,
                        Reasoning = $"No registered metric with key '{spec.MetricKey}'.",
                        Duration = TimeSpan.Zero
                    };
                }
                else
                {
                    try
                    {
                        score = await metric.ScoreAsync(@case, invocation, spec, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Metric {MetricKey} threw on case {CaseId}", spec.MetricKey, @case.Id);
                        score = new MetricScore
                        {
                            MetricKey = spec.MetricKey,
                            Score = 0.0,
                            Verdict = Verdict.Warn,
                            Reasoning = $"Metric threw: {ex.Message}",
                            Duration = TimeSpan.Zero
                        };
                    }
                }
                repeatScores.Add(score);
                totalCost += score.CostUsd;
            }
            scoresPerRepeat.Add(repeatScores);
        }

        sw.Stop();

        if (caseError is not null)
        {
            return new EvalResult
            {
                Case = @case,
                OutputPerRepeat = outputs,
                ScoresPerRepeat = scoresPerRepeat,
                AggregatedScores = new Dictionary<string, MetricScore>(),
                Verdict = Verdict.Fail,
                CostUsd = totalCost,
                Duration = sw.Elapsed,
                Error = caseError
            };
        }

        var aggregated = AggregateMedianByMetric(@case, scoresPerRepeat);
        var caseVerdict = WorstVerdict(aggregated.Values);

        return new EvalResult
        {
            Case = @case,
            OutputPerRepeat = outputs,
            ScoresPerRepeat = scoresPerRepeat,
            AggregatedScores = aggregated,
            Verdict = caseVerdict,
            CostUsd = totalCost,
            Duration = sw.Elapsed
        };
    }

    private static IReadOnlyDictionary<string, MetricScore> AggregateMedianByMetric(
        EvalCase @case,
        IReadOnlyList<IReadOnlyList<MetricScore>> scoresPerRepeat)
    {
        var byKey = new Dictionary<string, MetricScore>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in @case.MetricSpecs)
        {
            var samples = scoresPerRepeat
                .SelectMany(r => r.Where(s => string.Equals(s.MetricKey, spec.MetricKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (samples.Count == 0) continue;

            var medianScore = Median(samples.Select(s => s.Score).ToList());
            var verdict = medianScore >= spec.Threshold ? Verdict.Pass : Verdict.Fail;
            var anyWarn = samples.Any(s => s.Verdict == Verdict.Warn);

            // A single repeat's evidence carried whole (RawOutput/Consensus/Spread/
            // ViolatedClause/Evidence, via `with` so any field MetricScore gains later
            // rides along automatically) rather than synthesized from a mix of repeats —
            // a clause from repeat 1 paired with raw output from repeat 3 would
            // misattribute what a human reviewing a failure is looking at.
            var representative = RepresentativeSelector.PickClosest(samples, s => s.Score, medianScore);

            byKey[spec.MetricKey] = representative with
            {
                MetricKey = spec.MetricKey,
                Score = medianScore,
                Verdict = anyWarn ? Verdict.Warn : verdict,
                Reasoning = samples.Count == 1 ? samples[0].Reasoning : $"Median of {samples.Count} repeats.",
                CostUsd = samples.Sum(s => s.CostUsd),
                Duration = TimeSpan.FromTicks(samples.Sum(s => s.Duration.Ticks))
            };
        }
        return byKey;
    }

    private static Verdict WorstVerdict(IEnumerable<MetricScore> scores)
    {
        var list = scores.ToList();
        if (list.Count == 0) return Verdict.Warn;
        if (list.Any(s => s.Verdict == Verdict.Fail)) return Verdict.Fail;
        if (list.Any(s => s.Verdict == Verdict.Warn)) return Verdict.Warn;
        return Verdict.Pass;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static bool MatchesTagFilter(EvalCase @case, IReadOnlyList<string>? filter)
    {
        if (filter is null || filter.Count == 0) return true;
        return @case.Tags.Any(t => filter.Contains(t, StringComparer.OrdinalIgnoreCase));
    }
}
