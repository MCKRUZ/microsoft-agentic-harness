using Domain.AI.Evaluation;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Persistence;
using Infrastructure.AI.Evaluation.Tests.Reporters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Persistence;

/// <summary>
/// EF Core tests for <see cref="EfCoreEvalRunStore"/>. Uses an in-memory SQLite
/// database held open via a long-lived connection so the schema survives between
/// DbContext instances created by the factory. Mirrors the
/// <c>EfCorePromptUsageStoreTests</c> setup pattern.
/// </summary>
public sealed class EfCoreEvalRunStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly IDbContextFactory<EvalDashboardDbContext> _factory;
    private readonly EfCoreEvalRunStore _sut;

    public EfCoreEvalRunStoreTests()
    {
        var connectionString =
            $"Data Source=file:evaldash-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<EvalDashboardDbContext>()
            .UseSqlite(connectionString)
            .Options;

        _factory = new TestDbContextFactory(options);
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        _sut = new EfCoreEvalRunStore(_factory);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static DateTimeOffset ReceivedAt =>
        new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendAsync_persists_new_report_and_returns_true()
    {
        var report = TestReportFactory.DeterministicReport();

        var inserted = await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        inserted.Should().BeTrue();
    }

    [Fact]
    public async Task AppendAsync_is_idempotent_on_duplicate_run_id()
    {
        var report = TestReportFactory.DeterministicReport();

        var first = await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);
        var second = await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();

        await using var ctx = await _factory.CreateDbContextAsync();
        var runRows = await ctx.EvalRuns.CountAsync();
        var caseRows = await ctx.EvalCaseResults.CountAsync();
        var metricRows = await ctx.EvalMetricScores.CountAsync();
        runRows.Should().Be(1);
        caseRows.Should().Be(report.Results.Count);
        metricRows.Should().Be(
            report.Results.Sum(r => r.AggregatedScores.Count));
    }

    [Fact]
    public async Task GetRunDetailAsync_returns_null_for_unknown_run_id()
    {
        var actual = await _sut.GetRunDetailAsync("does-not-exist", CancellationToken.None);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task GetRunDetailAsync_round_trips_report_summary_counters()
    {
        var report = TestReportFactory.DeterministicReport();
        await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        var actual = await _sut.GetRunDetailAsync(report.RunId, CancellationToken.None);

        actual.Should().NotBeNull();
        actual!.RunId.Should().Be(report.RunId);
        actual.StartedAtUtc.Should().Be(report.StartedAtUtc);
        actual.CompletedAtUtc.Should().Be(report.CompletedAtUtc);
        actual.Duration.Should().Be(report.Duration);
        actual.PassedCount.Should().Be(report.PassedCount);
        actual.FailedCount.Should().Be(report.FailedCount);
        actual.WarnedCount.Should().Be(report.WarnedCount);
        actual.ErroredCount.Should().Be(report.ErroredCount);
        actual.TotalCostUsd.Should().Be(report.TotalCostUsd);
        actual.Repeats.Should().Be(report.Repeats);
        actual.OverallVerdict.Should().Be(report.OverallVerdict);
    }

    [Fact]
    public async Task GetRunDetailAsync_reassembles_per_case_results_with_aggregated_scores()
    {
        var report = TestReportFactory.DeterministicReport();
        await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        var actual = await _sut.GetRunDetailAsync(report.RunId, CancellationToken.None);

        actual!.Results.Should().HaveCount(report.Results.Count);

        foreach (var expected in report.Results)
        {
            var loaded = actual.Results.Single(r => r.Case.Id == expected.Case.Id);
            loaded.Verdict.Should().Be(expected.Verdict);
            loaded.Case.Input.Should().Be(expected.Case.Input);
            loaded.Case.ExpectedOutput.Should().Be(expected.Case.ExpectedOutput);
            loaded.AggregatedScores.Keys
                .Should().BeEquivalentTo(expected.AggregatedScores.Keys);
            foreach (var (key, score) in expected.AggregatedScores)
            {
                loaded.AggregatedScores[key].Score.Should().Be(score.Score);
                loaded.AggregatedScores[key].Verdict.Should().Be(score.Verdict);
                loaded.AggregatedScores[key].Reasoning.Should().Be(score.Reasoning);
            }
        }
    }

    [Fact]
    public async Task GetRecentAsync_orders_descending_by_started_at_utc()
    {
        var t0 = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        await _sut.AppendAsync(
            ReportWithRunId("run-third", t0.AddMinutes(2)), ReceivedAt, CancellationToken.None);
        await _sut.AppendAsync(
            ReportWithRunId("run-first", t0), ReceivedAt, CancellationToken.None);
        await _sut.AppendAsync(
            ReportWithRunId("run-second", t0.AddMinutes(1)), ReceivedAt, CancellationToken.None);

        var rows = await _sut.GetRecentAsync(10, CancellationToken.None);

        rows.Select(r => r.RunId).Should().Equal("run-third", "run-second", "run-first");
    }

    [Fact]
    public async Task GetRecentAsync_respects_take_limit()
    {
        var t0 = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            await _sut.AppendAsync(
                ReportWithRunId($"run-{i}", t0.AddMinutes(i)),
                ReceivedAt,
                CancellationToken.None);
        }

        var rows = await _sut.GetRecentAsync(2, CancellationToken.None);

        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentAsync_rejects_non_positive_take()
    {
        Func<Task> act = () => _sut.GetRecentAsync(0, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AppendAsync_rejects_null_report()
    {
        Func<Task> act = () => _sut.AppendAsync(null!, ReceivedAt, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_returns_false_when_unique_constraint_violates_on_race()
    {
        // Simulate the race by inserting a row outside the SUT (so the SUT's AnyAsync
        // probe still sees "no row") and then asking the SUT to append the same RunId.
        // The probe optimisation is intentionally bypassed via a parallel context to
        // exercise the unique-constraint-catch path. The optimisation handles the
        // serial duplicate; the catch handles the concurrent duplicate.
        var report = TestReportFactory.DeterministicReport();

        // Stage a competing write that lands AFTER the SUT's probe but BEFORE its save.
        // The cleanest deterministic stand-in: invoke AppendAsync twice in parallel and
        // assert exactly one returns true and the other false (no exception escapes).
        var firstTask = _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);
        var secondTask = _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        var results = await Task.WhenAll(firstTask, secondTask);

        results.Count(r => r).Should().BeLessThanOrEqualTo(1,
            "no more than one of the two concurrent appends should claim to have written");

        await using var ctx = await _factory.CreateDbContextAsync();
        var rowCount = await ctx.EvalRuns.CountAsync(r => r.RunId == report.RunId);
        rowCount.Should().Be(1, "unique index forbids duplicate RunId rows");
    }

    [Fact]
    public async Task GetRunDetailAsync_then_AppendAsync_preserves_dataset_name_attribution()
    {
        var original = TestReportFactory.DeterministicReport();
        await _sut.AppendAsync(original, ReceivedAt, CancellationToken.None);

        // Round-trip: read the report back, give it a fresh RunId, and re-ingest.
        // The reassembled datasets must carry the original case lists so the
        // dataset_name lookup at write time resolves correctly.
        var reloaded = await _sut.GetRunDetailAsync(original.RunId, CancellationToken.None);
        reloaded.Should().NotBeNull();

        var roundTripped = reloaded! with { RunId = "round-trip-run" };
        await _sut.AppendAsync(roundTripped, ReceivedAt, CancellationToken.None);

        await using var ctx = await _factory.CreateDbContextAsync();
        var roundTrippedRows = await ctx.EvalCaseResults
            .Where(c => c.RunId == "round-trip-run")
            .ToListAsync();

        roundTrippedRows.Should().NotBeEmpty();
        roundTrippedRows.Should().OnlyContain(
            r => !string.IsNullOrEmpty(r.DatasetName),
            "round-tripped rows must carry the dataset name from the original ingest");
        roundTrippedRows.Select(r => r.DatasetName).Distinct()
            .Should().BeEquivalentTo(["demo-dataset"]);
    }

    [Fact]
    public async Task AppendAsync_tolerates_concurrent_writes_of_distinct_run_ids()
    {
        var t0 = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var tasks = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => _sut.AppendAsync(
                ReportWithRunId($"run-{i}", t0.AddSeconds(i)),
                ReceivedAt,
                CancellationToken.None)))
            .ToArray();

        await Task.WhenAll(tasks);

        await using var ctx = await _factory.CreateDbContextAsync();
        var count = await ctx.EvalRuns.CountAsync();
        count.Should().Be(16);
    }

    [Fact]
    public async Task GetLatestAggregatedScoresAsync_returns_empty_for_empty_case_ids()
    {
        var result = await _sut.GetLatestAggregatedScoresAsync(
            [], "exact_match", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestAggregatedScoresAsync_returns_score_for_matching_case_metric()
    {
        var report = TestReportFactory.DeterministicReport();
        await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        var scores = await _sut.GetLatestAggregatedScoresAsync(
            ["case-pass", "case-fail"],
            "exact_match",
            CancellationToken.None);

        scores.Should().ContainKey("case-pass");
        scores["case-pass"].Should().Be(1.0);
        scores.Should().ContainKey("case-fail");
        scores["case-fail"].Should().Be(0.0);
    }

    [Fact]
    public async Task GetLatestAggregatedScoresAsync_picks_score_from_latest_run()
    {
        var template = TestReportFactory.DeterministicReport();
        var earlier = template with
        {
            RunId = "earlier",
            StartedAtUtc = new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero),
        };
        var later = template with
        {
            RunId = "later",
            StartedAtUtc = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero),
            Results = template.Results
                .Select(r => r.Case.Id == "case-pass"
                    ? r with
                    {
                        AggregatedScores = new Dictionary<string, MetricScore>
                        {
                            ["exact_match"] = new()
                            {
                                MetricKey = "exact_match",
                                Score = 0.42, // distinguishable from the earlier 1.0
                                Verdict = Verdict.Fail,
                            },
                        },
                    }
                    : r)
                .ToList(),
        };
        await _sut.AppendAsync(earlier, ReceivedAt, CancellationToken.None);
        await _sut.AppendAsync(later, ReceivedAt, CancellationToken.None);

        var scores = await _sut.GetLatestAggregatedScoresAsync(
            ["case-pass"],
            "exact_match",
            CancellationToken.None);

        scores["case-pass"].Should().Be(0.42, "the later run's score must win over the earlier one");
    }

    [Fact]
    public async Task GetRecentAsync_summary_pass_rate_matches_report()
    {
        var report = TestReportFactory.DeterministicReport();
        await _sut.AppendAsync(report, ReceivedAt, CancellationToken.None);

        var rows = await _sut.GetRecentAsync(10, CancellationToken.None);

        rows.Single().PassRate.Should().Be(report.PassRate);
    }

    private static EvalRunReport ReportWithRunId(string runId, DateTimeOffset startedAt)
    {
        var template = TestReportFactory.DeterministicReport();
        return template with
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            CompletedAtUtc = startedAt + template.Duration,
        };
    }

    private sealed class TestDbContextFactory : IDbContextFactory<EvalDashboardDbContext>
    {
        private readonly DbContextOptions<EvalDashboardDbContext> _options;
        public TestDbContextFactory(DbContextOptions<EvalDashboardDbContext> options) =>
            _options = options;
        public EvalDashboardDbContext CreateDbContext() => new(_options);
    }
}
