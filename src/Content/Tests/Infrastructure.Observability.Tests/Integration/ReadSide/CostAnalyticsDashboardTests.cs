using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

[Collection("Postgres")]
public sealed class CostAnalyticsDashboardTests
{
    private readonly PostgresFixture _fixture;

    public CostAnalyticsDashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TotalSpend_ReturnsSumOfCosts()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "CostAgent1", costUsd: 0.50m);
        await builder.CreateSessionAsync(agentName: "CostAgent2", costUsd: 0.75m);
        await builder.CreateSessionAsync(agentName: "CostAgent3", costUsd: 1.25m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var total = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Cost_TotalSpend,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(total >= 2.50m);
    }

    [Fact]
    public async Task DailyAverage_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(costUsd: 1.00m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<double>(
            DashboardQueries.Cost_DailyAverage,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(avg > 0);
    }

    [Fact]
    public async Task CostPerSession_ReturnsAverage()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(costUsd: 0.40m);
        await builder.CreateSessionAsync(costUsd: 0.60m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Cost_PerSession,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(avg > 0m);
    }

    [Fact]
    public async Task DailyTrend_AfterMvRefresh_ReturnsRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(costUsd: 0.80m);
        await builder.RefreshDailyCostSummaryAsync();

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_DailyTrend,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task DailyTokenTrend_AfterMvRefresh_ReturnsRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(inputTokens: 5000, outputTokens: 2000);
        await builder.RefreshDailyCostSummaryAsync();

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_DailyTokenTrend,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task CostByAgent_ReturnsGroupedRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "CostByAgentA", costUsd: 0.30m);
        await builder.CreateSessionAsync(agentName: "CostByAgentB", costUsd: 0.70m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_ByAgent,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 2);
        var agents = rows.Select(r => (string?)r["agent_name"]).ToList();
        Assert.Contains("CostByAgentA", agents);
        Assert.Contains("CostByAgentB", agents);
    }

    [Fact]
    public async Task CostByModel_ReturnsGroupedRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(model: "gpt-4o", costUsd: 0.20m);
        await builder.CreateSessionAsync(model: "claude-opus", costUsd: 0.50m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_ByModel,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(rows.Count >= 2);
    }

    [Fact]
    public async Task TokenBreakdown_ReturnsAllFourFields()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(
            inputTokens: 800, outputTokens: 400, cacheRead: 150, cacheWrite: 75);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_TokenBreakdown,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.ContainsKey("input"));
        Assert.True(row.ContainsKey("output"));
        Assert.True(row.ContainsKey("cache_read"));
        Assert.True(row.ContainsKey("cache_write"));
        Assert.True((long)row["input"]! >= 800);
        Assert.True((long)row["output"]! >= 400);
    }

    [Fact]
    public async Task TokenUsageOverTime_AfterMvRefresh_ReturnsRows()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(inputTokens: 3000, outputTokens: 1500);
        await builder.RefreshDailyCostSummaryAsync();

        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_TokenUsageOverTime,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task TopExpensive_ReturnsOrderedByCost()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "CostExpensiveA", costUsd: 0.10m);
        await builder.CreateSessionAsync(agentName: "CostExpensiveB", costUsd: 2.00m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_TopExpensive,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"));

        Assert.True(rows.Count >= 2);

        // Verify DESC ordering by cost
        for (var i = 1; i < rows.Count; i++)
        {
            var prev = Convert.ToDecimal(rows[i - 1]["total_cost_usd"]);
            var curr = Convert.ToDecimal(rows[i]["total_cost_usd"]);
            Assert.True(prev >= curr);
        }
    }

    [Fact]
    public async Task VarAgentName_ReturnsDistinctAgents()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "CostVarAgent");

        var rows = await _fixture.QueryRowsAsync(DashboardQueries.Cost_VarAgentName);

        Assert.Contains(rows, r => (string?)r["agent_name"] == "CostVarAgent");
    }
}
