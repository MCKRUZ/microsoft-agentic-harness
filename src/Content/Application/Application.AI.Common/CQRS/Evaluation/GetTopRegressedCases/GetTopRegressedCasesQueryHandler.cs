using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Evaluation.GetTopRegressedCases;

/// <summary>
/// Handles <see cref="GetTopRegressedCasesQuery"/> by loading both runs from
/// <see cref="IEvalRunStore"/>, joining their results on
/// (<c>case_id</c>, <c>metric_key</c>), and surfacing the largest negative deltas.
/// </summary>
public sealed class GetTopRegressedCasesQueryHandler
    : IRequestHandler<GetTopRegressedCasesQuery, Result<IReadOnlyList<RegressedCaseRow>>>
{
    private readonly IEvalRunStore _store;
    private readonly ILogger<GetTopRegressedCasesQueryHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    public GetTopRegressedCasesQueryHandler(
        IEvalRunStore store,
        ILogger<GetTopRegressedCasesQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RegressedCaseRow>>> Handle(
        GetTopRegressedCasesQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        EvalRunReport? baseline, current;
        try
        {
            // Sequential loads (vs Task.WhenAll) keep the store contract simple — both
            // runs are typically small enough that the extra round-trip latency is
            // negligible against the SQL query cost itself. Revisit if profiling shows
            // detail-load as a hot spot.
            baseline = await _store.GetRunDetailAsync(request.BaselineRunId, cancellationToken).ConfigureAwait(false);
            current = await _store.GetRunDetailAsync(request.CurrentRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load runs for regression comparison ({Current} vs {Baseline}).",
                request.CurrentRunId,
                request.BaselineRunId);
            return Result<IReadOnlyList<RegressedCaseRow>>.Fail(
                $"Failed to load runs: {ex.Message}");
        }

        if (baseline is null)
        {
            return Result<IReadOnlyList<RegressedCaseRow>>.NotFound(
                $"Baseline run '{request.BaselineRunId}' not found.");
        }
        if (current is null)
        {
            return Result<IReadOnlyList<RegressedCaseRow>>.NotFound(
                $"Current run '{request.CurrentRunId}' not found.");
        }

        // Index baseline scores by (case_id, metric_key). Same lookup shape used for
        // current; the join is symmetric over the keys present in both.
        var baselineIndex = IndexAggregatedScores(baseline);
        var currentIndex = IndexAggregatedScores(current);

        var regressions = new List<RegressedCaseRow>();
        foreach (var (key, currentScore) in currentIndex)
        {
            if (!baselineIndex.TryGetValue(key, out var baselineScore))
            {
                // Case + metric only in current: not a regression, skip.
                continue;
            }

            var delta = currentScore.Score - baselineScore.Score;
            if (delta >= 0)
            {
                // Improvement or no change: not a regression. Strict < 0 threshold so
                // floating-point equality doesn't flap rows in/out across reruns.
                continue;
            }

            regressions.Add(new RegressedCaseRow
            {
                CaseId = key.CaseId,
                DatasetName = currentScore.DatasetName,
                MetricKey = key.MetricKey,
                BaselineScore = baselineScore.Score,
                CurrentScore = currentScore.Score,
            });
        }

        var ordered = regressions
            .OrderBy(r => r.Delta) // most negative first
            .ThenBy(r => r.CaseId, StringComparer.Ordinal)
            .Take(request.Take)
            .ToList();

        return Result<IReadOnlyList<RegressedCaseRow>>.Success(ordered);
    }

    private static Dictionary<(string CaseId, string MetricKey), (double Score, string DatasetName)>
        IndexAggregatedScores(EvalRunReport report)
    {
        // Build (case_id → dataset_name) once via the reassembled Datasets graph.
        // Store.GetRunDetailAsync populates Cases on each EvalDataset from the
        // persisted rows so this walk is authoritative without a second query.
        var datasetByCaseId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dataset in report.Datasets)
        {
            foreach (var c in dataset.Cases)
            {
                datasetByCaseId.TryAdd(c.Id, dataset.Name);
            }
        }

        var index = new Dictionary<(string CaseId, string MetricKey), (double Score, string DatasetName)>();
        foreach (var result in report.Results)
        {
            var datasetName = datasetByCaseId.TryGetValue(result.Case.Id, out var name)
                ? name
                : string.Empty;

            foreach (var (metricKey, score) in result.AggregatedScores)
            {
                index[(result.Case.Id, metricKey)] = (score.Score, datasetName);
            }
        }
        return index;
    }
}
