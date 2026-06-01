using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Evaluation.GetPromptVersionComparison;

/// <summary>
/// Handles <see cref="GetPromptVersionComparisonQuery"/> by joining the
/// prompt-usage log against the eval metric-score store.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm:
/// <list type="number">
///   <item><description>Load every usage row for the supplied prompt name.</description></item>
///   <item><description>Group rows by (version, metric_key) — the same prompt may power multiple metrics.</description></item>
///   <item><description>For each (version, metric_key) bucket, look up the latest aggregated score per case via <see cref="IEvalRunStore.GetLatestAggregatedScoresAsync"/>.</description></item>
///   <item><description>Compute the arithmetic mean and sample size; emit one row per bucket.</description></item>
/// </list>
/// </para>
/// <para>
/// Usage rows that lack a <see cref="PromptUsageRecord.MetricKey"/> are silently
/// skipped — they didn't come from a metric-evaluator surface and have no
/// corresponding eval score to join against.
/// </para>
/// </remarks>
public sealed class GetPromptVersionComparisonQueryHandler
    : IRequestHandler<GetPromptVersionComparisonQuery, Result<IReadOnlyList<PromptVersionComparisonRow>>>
{
    private readonly IPromptUsageStore _usageStore;
    private readonly IEvalRunStore _evalStore;
    private readonly ILogger<GetPromptVersionComparisonQueryHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    public GetPromptVersionComparisonQueryHandler(
        IPromptUsageStore usageStore,
        IEvalRunStore evalStore,
        ILogger<GetPromptVersionComparisonQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(evalStore);
        ArgumentNullException.ThrowIfNull(logger);

        _usageStore = usageStore;
        _evalStore = evalStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PromptVersionComparisonRow>>> Handle(
        GetPromptVersionComparisonQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<PromptUsageRecord> usageRows;
        try
        {
            usageRows = await _usageStore
                .QueryByPromptNameAsync(request.PromptName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query prompt-usage rows for {Prompt}.", request.PromptName);
            return Result<IReadOnlyList<PromptVersionComparisonRow>>.Fail(
                $"Failed to load prompt-usage rows: {ex.Message}");
        }

        // Group by (version, metric_key). Skip rows without a metric_key — they came
        // from non-metric surfaces (CQRS handlers, agent factory) and have no eval
        // score to aggregate against.
        var buckets = usageRows
            .Where(r => !string.IsNullOrEmpty(r.MetricKey) && !string.IsNullOrEmpty(r.CaseId))
            .GroupBy(r => (Version: r.Descriptor.Version, MetricKey: r.MetricKey!))
            .ToList();

        var output = new List<PromptVersionComparisonRow>(buckets.Count);
        foreach (var bucket in buckets)
        {
            var caseIds = bucket.Select(r => r.CaseId!).Distinct(StringComparer.Ordinal).ToList();
            IReadOnlyDictionary<string, double> scores;
            try
            {
                scores = await _evalStore
                    .GetLatestAggregatedScoresAsync(caseIds, bucket.Key.MetricKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to fetch aggregated scores for metric {Metric} on {Count} cases.",
                    bucket.Key.MetricKey,
                    caseIds.Count);
                return Result<IReadOnlyList<PromptVersionComparisonRow>>.Fail(
                    $"Failed to load eval scores: {ex.Message}");
            }

            if (scores.Count == 0)
            {
                // No score rows found — emit a row anyway so the UI can show "version
                // observed but no matching evals." Average 0, sample size 0.
                output.Add(new PromptVersionComparisonRow
                {
                    Version = bucket.Key.Version,
                    MetricKey = bucket.Key.MetricKey,
                    AverageScore = 0.0,
                    SampleSize = 0,
                });
                continue;
            }

            var average = scores.Values.Average();
            output.Add(new PromptVersionComparisonRow
            {
                Version = bucket.Key.Version,
                MetricKey = bucket.Key.MetricKey,
                AverageScore = average,
                SampleSize = scores.Count,
            });
        }

        // Stable ordering: version descending so newest appears first.
        var ordered = output
            .OrderByDescending(r => r.Version)
            .ThenBy(r => r.MetricKey, StringComparer.Ordinal)
            .ToList();

        return Result<IReadOnlyList<PromptVersionComparisonRow>>.Success(ordered);
    }
}
