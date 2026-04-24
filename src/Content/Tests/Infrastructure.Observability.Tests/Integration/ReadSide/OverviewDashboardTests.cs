using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

[Collection("Postgres")]
public sealed class OverviewDashboardTests
{
    private readonly PostgresFixture _fixture;

    public OverviewDashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TotalSessions_ReturnsAtLeastTwo()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "OverviewAgentA", status: "completed");
        await builder.CreateSessionAsync(agentName: "OverviewAgentB", status: "error");

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var count = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Overview_TotalSessions,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(count >= 2);
    }

    [Fact]
    public async Task TotalCost_ReturnsSumOfSeededCosts()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "OverviewCostA", costUsd: 0.50m);
        await builder.CreateSessionAsync(agentName: "OverviewCostB", costUsd: 1.00m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var total = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Overview_TotalCost,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(total >= 1.50m);
    }

    [Fact]
    public async Task TotalTokens_ReturnsSumOfSeededTokens()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(inputTokens: 2000, outputTokens: 1000);
        await builder.CreateSessionAsync(inputTokens: 3000, outputTokens: 1500);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var total = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Overview_TotalTokens,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(total >= 7500);
    }

    [Fact]
    public async Task AvgCacheHit_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(cacheHitRate: 0.40m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Overview_AvgCacheHit,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(avg > 0m);
    }

    [Fact]
    public async Task ErrorRate_WithOneError_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "OverviewErrOk", status: "completed");
        await builder.CreateSessionAsync(agentName: "OverviewErrFail", status: "error");

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rate = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Overview_ErrorRate,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rate > 0m);
    }

    [Fact]
    public async Task SessionsOverTime_ReturnsAtLeastOneTimeBucket()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_SessionsOverTime,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task CostOverTime_AfterMvRefresh_ReturnsRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(costUsd: 0.75m);
        await builder.RefreshDailyCostSummaryAsync();

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_CostOverTime,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task TokenDistribution_ReturnsAllFourFields()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(
            inputTokens: 500, outputTokens: 250, cacheRead: 100, cacheWrite: 50);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_TokenDistribution,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.ContainsKey("input"));
        Assert.True(row.ContainsKey("output"));
        Assert.True(row.ContainsKey("cache_read"));
        Assert.True(row.ContainsKey("cache_write"));
    }

    [Fact]
    public async Task RecentSessions_ReturnsOrderedRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "OverviewRecentA");
        await builder.CreateSessionAsync(agentName: "OverviewRecentB");

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_RecentSessions,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 2);

        // Verify DESC ordering
        for (var i = 1; i < rows.Count; i++)
        {
            var prev = (DateTime)rows[i - 1]["started_at"]!;
            var curr = (DateTime)rows[i]["started_at"]!;
            Assert.True(prev >= curr);
        }
    }

    [Fact]
    public async Task CostByModel_ReturnsGroupedRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(model: "gpt-4o", costUsd: 0.30m);
        await builder.CreateSessionAsync(model: "claude-sonnet", costUsd: 0.20m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_CostByModel,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 2);
        var models = rows.Select(r => (string?)r["model"]).ToList();
        Assert.Contains("gpt-4o", models);
        Assert.Contains("claude-sonnet", models);
    }
}
