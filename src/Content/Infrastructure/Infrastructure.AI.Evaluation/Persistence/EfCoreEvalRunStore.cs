using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IEvalRunStore"/> over
/// <see cref="EvalDashboardDbContext"/>. Uses <see cref="IDbContextFactory{TContext}"/>
/// for short-lived contexts so the store can be a singleton without leaking a shared
/// DbContext across threads.
/// </summary>
public sealed class EfCoreEvalRunStore : IEvalRunStore
{
    // Cached options match the rest of the harness (camelCase). Stored as a static
    // singleton because creating JsonSerializerOptions per-call is allocation-heavy
    // and the configuration is invariant.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<EvalDashboardDbContext> _contextFactory;

    /// <summary>Initializes a new instance.</summary>
    public EfCoreEvalRunStore(IDbContextFactory<EvalDashboardDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<bool> AppendAsync(
        EvalRunReport report,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Idempotency fast path: if a row with this RunId already exists, return false
        // without building the entity graph. The unique index on RunId is the source of
        // truth — the probe is an optimisation, not a correctness guard. Concurrent
        // appends of the same RunId both passing this probe is handled below by
        // catching the unique-constraint violation from SaveChangesAsync.
        var existing = await context.EvalRuns
            .AsNoTracking()
            .AnyAsync(e => e.RunId == report.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            return false;
        }

        // Build dataset-name lookup once. O(D × C) build, O(1) per result lookup, vs
        // O(D × C × R) when each result scans every dataset's case list. Captures the
        // (case_id → dataset_name) attribution at ingest time so reads never re-scan.
        var datasetNameByCaseId = BuildDatasetNameLookup(report.Datasets);

        var runEntity = new EvalRunEntity
        {
            RunId = report.RunId,
            StartedAtUtc = report.StartedAtUtc,
            CompletedAtUtc = report.CompletedAtUtc,
            DurationTicks = report.Duration.Ticks,
            PassedCount = report.PassedCount,
            FailedCount = report.FailedCount,
            WarnedCount = report.WarnedCount,
            ErroredCount = report.ErroredCount,
            TotalCostUsd = report.TotalCostUsd,
            Repeats = report.Repeats,
            OverallVerdict = (int)report.OverallVerdict,
            DatasetsJson = SerializeDatasetMetadata(report.Datasets),
            WarningsJson = JsonSerializer.Serialize(report.Warnings, JsonOptions),
            ReceivedAtUtc = receivedAtUtc,
        };
        context.EvalRuns.Add(runEntity);

        foreach (var result in report.Results)
        {
            var datasetName = datasetNameByCaseId.TryGetValue(result.Case.Id, out var name)
                ? name
                : string.Empty;
            var caseEntity = new EvalCaseResultEntity
            {
                RunId = report.RunId,
                DatasetName = datasetName,
                CaseId = result.Case.Id,
                Input = result.Case.Input,
                ExpectedOutput = result.Case.ExpectedOutput,
                RetrievedContext = result.Case.RetrievedContext,
                TagsJson = JsonSerializer.Serialize(result.Case.Tags, JsonOptions),
                InvocationOverridesJson = JsonSerializer.Serialize(
                    result.Case.InvocationOverrides, JsonOptions),
                MetricSpecsJson = JsonSerializer.Serialize(result.Case.MetricSpecs, JsonOptions),
                OutputPerRepeatJson = JsonSerializer.Serialize(result.OutputPerRepeat, JsonOptions),
                ScoresPerRepeatJson = JsonSerializer.Serialize(result.ScoresPerRepeat, JsonOptions),
                Verdict = (int)result.Verdict,
                CostUsd = result.CostUsd,
                DurationTicks = result.Duration.Ticks,
                Error = result.Error,
            };
            context.EvalCaseResults.Add(caseEntity);

            foreach (var (metricKey, score) in result.AggregatedScores)
            {
                context.EvalMetricScores.Add(new EvalMetricScoreEntity
                {
                    RunId = report.RunId,
                    CaseId = result.Case.Id,
                    MetricKey = metricKey,
                    Score = score.Score,
                    Verdict = (int)score.Verdict,
                    Reasoning = score.Reasoning,
                    CostUsd = score.CostUsd,
                    DurationTicks = score.Duration.Ticks,
                });
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Concurrent appends of the same RunId: both probed empty, both staged inserts,
            // one won the unique-index write, the other (us) lost. Honours the interface
            // contract by collapsing the race to the same outcome as the fast-path probe:
            // duplicate ingest is a no-op returning false.
            return false;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite error code 19 = SQLITE_CONSTRAINT. The unique index ux_eval_runs_run_id
        // is the only unique constraint the schema declares, so any constraint violation
        // on this DbContext is necessarily a duplicate RunId. Narrow check guards against
        // accidentally swallowing a future constraint violation we'd want to surface.
        return ex.InnerException is SqliteException sqlite
               && sqlite.SqliteErrorCode == 19;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvalRunSummary>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be positive.");
        }

        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await context.EvalRuns
            .AsNoTracking()
            .OrderByDescending(e => e.StartedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ConvertAll(ToSummary);
    }

    /// <inheritdoc />
    public async Task<EvalRunReport?> GetRunDetailAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var runEntity = await context.EvalRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.RunId == runId, cancellationToken)
            .ConfigureAwait(false);

        if (runEntity is null)
        {
            return null;
        }

        var caseRows = await context.EvalCaseResults
            .AsNoTracking()
            .Where(e => e.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var metricRows = await context.EvalMetricScores
            .AsNoTracking()
            .Where(e => e.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Group per-case aggregated scores by case id so reassembly into EvalResult is O(rows).
        var scoresByCase = new Dictionary<string, Dictionary<string, MetricScore>>(StringComparer.Ordinal);
        foreach (var row in metricRows)
        {
            if (!scoresByCase.TryGetValue(row.CaseId, out var perMetric))
            {
                perMetric = new Dictionary<string, MetricScore>(StringComparer.Ordinal);
                scoresByCase[row.CaseId] = perMetric;
            }
            perMetric[row.MetricKey] = new MetricScore
            {
                MetricKey = row.MetricKey,
                Score = row.Score,
                Verdict = (Verdict)row.Verdict,
                Reasoning = row.Reasoning,
                CostUsd = row.CostUsd,
                Duration = TimeSpan.FromTicks(row.DurationTicks),
            };
        }

        var warnings = JsonSerializer.Deserialize<List<string>>(runEntity.WarningsJson, JsonOptions)
                       ?? [];

        var results = caseRows.ConvertAll(c => ToResult(c, scoresByCase));

        // Rebuild datasets with their case lists populated from the persisted rows.
        // Necessary so a round-trip (GetRunDetailAsync → AppendAsync) preserves the
        // (case_id → dataset_name) attribution that the lookup at write time depends on.
        var datasets = ReassembleDatasetsWithCases(runEntity.DatasetsJson, caseRows, results);

        return new EvalRunReport
        {
            RunId = runEntity.RunId,
            StartedAtUtc = runEntity.StartedAtUtc,
            CompletedAtUtc = runEntity.CompletedAtUtc,
            Duration = TimeSpan.FromTicks(runEntity.DurationTicks),
            Datasets = datasets,
            Results = results,
            PassedCount = runEntity.PassedCount,
            FailedCount = runEntity.FailedCount,
            WarnedCount = runEntity.WarnedCount,
            ErroredCount = runEntity.ErroredCount,
            TotalCostUsd = runEntity.TotalCostUsd,
            Repeats = runEntity.Repeats,
            OverallVerdict = (Verdict)runEntity.OverallVerdict,
            Warnings = warnings,
        };
    }

    private static EvalRunSummary ToSummary(EvalRunEntity e) => new()
    {
        RunId = e.RunId,
        StartedAtUtc = e.StartedAtUtc,
        CompletedAtUtc = e.CompletedAtUtc,
        Duration = TimeSpan.FromTicks(e.DurationTicks),
        PassedCount = e.PassedCount,
        FailedCount = e.FailedCount,
        WarnedCount = e.WarnedCount,
        ErroredCount = e.ErroredCount,
        TotalCostUsd = e.TotalCostUsd,
        Repeats = e.Repeats,
        OverallVerdict = (Verdict)e.OverallVerdict,
        ReceivedAtUtc = e.ReceivedAtUtc,
    };

    private static EvalResult ToResult(
        EvalCaseResultEntity row,
        IReadOnlyDictionary<string, Dictionary<string, MetricScore>> scoresByCase)
    {
        var tags = JsonSerializer.Deserialize<List<string>>(row.TagsJson, JsonOptions) ?? [];
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(
            row.InvocationOverridesJson, JsonOptions) ?? [];
        var specs = JsonSerializer.Deserialize<List<MetricSpec>>(row.MetricSpecsJson, JsonOptions)
                    ?? [];
        var outputs = JsonSerializer.Deserialize<List<string>>(row.OutputPerRepeatJson, JsonOptions)
                      ?? [];
        var scoresPerRepeat = JsonSerializer.Deserialize<List<List<MetricScore>>>(
            row.ScoresPerRepeatJson, JsonOptions) ?? [];

        var aggregated = scoresByCase.TryGetValue(row.CaseId, out var perMetric)
            ? perMetric
            : new Dictionary<string, MetricScore>(StringComparer.Ordinal);

        var evalCase = new EvalCase
        {
            Id = row.CaseId,
            Input = row.Input,
            ExpectedOutput = row.ExpectedOutput,
            RetrievedContext = row.RetrievedContext,
            Tags = tags,
            InvocationOverrides = overrides,
            MetricSpecs = specs,
        };

        return new EvalResult
        {
            Case = evalCase,
            OutputPerRepeat = outputs,
            ScoresPerRepeat = scoresPerRepeat.ConvertAll(inner => (IReadOnlyList<MetricScore>)inner),
            AggregatedScores = aggregated,
            Verdict = (Verdict)row.Verdict,
            CostUsd = row.CostUsd,
            Duration = TimeSpan.FromTicks(row.DurationTicks),
            Error = row.Error,
        };
    }

    private static string SerializeDatasetMetadata(IReadOnlyList<EvalDataset> datasets)
    {
        // Store dataset metadata only — case bodies live in eval_case_results.
        // The compact projection keeps the runs table light for list queries.
        var projected = datasets.Select(d => new DatasetMetadataDto
        {
            Name = d.Name,
            Version = d.Version,
            Description = d.Description,
            SourcePath = d.SourcePath,
            CaseCount = d.Cases.Count,
        }).ToList();
        return JsonSerializer.Serialize(projected, JsonOptions);
    }

    private static IReadOnlyList<EvalDataset> ReassembleDatasetsWithCases(
        string datasetsJson,
        IReadOnlyList<EvalCaseResultEntity> caseRows,
        IReadOnlyList<EvalResult> results)
    {
        var projected = JsonSerializer.Deserialize<List<DatasetMetadataDto>>(datasetsJson, JsonOptions)
                        ?? [];

        // Index the reassembled EvalCases by id so each dataset can claim its own.
        // Built once outside the dataset loop so reassembly stays O(C + D).
        var caseById = new Dictionary<string, EvalCase>(results.Count, StringComparer.Ordinal);
        foreach (var result in results)
        {
            caseById[result.Case.Id] = result.Case;
        }

        // Group case rows by their persisted dataset_name so each EvalDataset
        // reassembles with the exact case list it carried at ingest time. A
        // subsequent re-ingest of this report rebuilds the same dataset → case
        // lookup, preserving DatasetName attribution end-to-end.
        var casesByDataset = new Dictionary<string, List<EvalCase>>(StringComparer.Ordinal);
        foreach (var row in caseRows)
        {
            if (!casesByDataset.TryGetValue(row.DatasetName, out var list))
            {
                list = [];
                casesByDataset[row.DatasetName] = list;
            }
            if (caseById.TryGetValue(row.CaseId, out var evalCase))
            {
                list.Add(evalCase);
            }
        }

        return projected.ConvertAll(p => new EvalDataset
        {
            Name = p.Name,
            Version = p.Version,
            Description = p.Description,
            SourcePath = p.SourcePath,
            Cases = casesByDataset.TryGetValue(p.Name, out var cases)
                ? cases
                : (IReadOnlyList<EvalCase>)[],
        });
    }

    private static Dictionary<string, string> BuildDatasetNameLookup(
        IReadOnlyList<EvalDataset> datasets)
    {
        // First-write-wins on case-id collision across datasets — matches the original
        // ResolveDatasetName behaviour (which returned the first dataset containing the id).
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dataset in datasets)
        {
            for (var i = 0; i < dataset.Cases.Count; i++)
            {
                var caseId = dataset.Cases[i].Id;
                if (!lookup.ContainsKey(caseId))
                {
                    lookup[caseId] = dataset.Name;
                }
            }
        }
        return lookup;
    }

    private sealed record DatasetMetadataDto
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public string? Description { get; init; }
        public string? SourcePath { get; init; }
        public int CaseCount { get; init; }
    }
}
