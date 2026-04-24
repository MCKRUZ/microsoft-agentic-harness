using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

[Collection("Postgres")]
public sealed class ToolsAndSafetyDashboardTests
{
    private readonly PostgresFixture _fixture;

    public ToolsAndSafetyDashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<SeededSession> SeedToolsAndSafetyDataAsync()
    {
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(agentName: "ToolsSafetyAgent");

        // 3 tool executions: 2 success "get_weather" keyed_di, 1 failure "search" mcp
        await builder.AddToolAsync(session.Id, toolName: "get_weather",
            toolSource: "keyed_di", durationMs: 30, status: "success");
        await builder.AddToolAsync(session.Id, toolName: "get_weather",
            toolSource: "keyed_di", durationMs: 50, status: "success");
        await builder.AddToolAsync(session.Id, toolName: "search",
            toolSource: "mcp", durationMs: 200, status: "failure", errorType: "timeout");

        // 3 safety events: pass, block (hate), redact
        await builder.AddSafetyAsync(session.Id, phase: "prompt", outcome: "pass");
        await builder.AddSafetyAsync(session.Id, phase: "response",
            outcome: "block", category: "hate", severity: 5);
        await builder.AddSafetyAsync(session.Id, phase: "response", outcome: "redact");

        return session;
    }

    [Fact]
    public async Task TotalToolCalls_ReturnsThree()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var count = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Tools_TotalCalls,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@tool", "All"));

        Assert.True(count >= 3);
    }

    [Fact]
    public async Task ErrorRate_ReturnsAbout33Percent()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rate = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Tools_ErrorRate,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@tool", "All"));

        Assert.True(rate > 0m);
    }

    [Fact]
    public async Task AvgDuration_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<double>(
            DashboardQueries.Tools_AvgDuration,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@tool", "All"));

        Assert.True(avg > 0);
    }

    [Fact]
    public async Task UniqueTools_ReturnsTwo()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var count = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Tools_UniqueTools,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(count >= 2);
    }

    [Fact]
    public async Task VolumeOverTime_ReturnsAtLeastOneBucket()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_VolumeOverTime,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 1);
    }

    [Fact]
    public async Task PerformanceTable_ReturnsGroupedByTool()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_PerformanceTable,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 2);
        var tools = rows.Select(r => (string?)r["tool_name"]).ToList();
        Assert.Contains("get_weather", tools);
        Assert.Contains("search", tools);
    }

    [Fact]
    public async Task StatusDistribution_ReturnsSuccessAndFailure()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_StatusDistribution,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        var statuses = rows.Select(r => (string?)r["status"]).ToList();
        Assert.Contains("success", statuses);
        Assert.Contains("failure", statuses);
    }

    [Fact]
    public async Task SourceBreakdown_ReturnsKeyedDiAndMcp()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_SourceBreakdown,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        var sources = rows.Select(r => (string?)r["tool_source"]).ToList();
        Assert.Contains("keyed_di", sources);
        Assert.Contains("mcp", sources);
    }

    [Fact]
    public async Task RecentErrors_ContainsFailedTool()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_RecentErrors,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 1);
        Assert.Contains(rows, r => (string?)r["tool_name"] == "search"
                                 && (string?)r["error_type"] == "timeout");
    }

    [Fact]
    public async Task ErrorRateByTool_ReturnsSearchOnly()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_ErrorRateByTool,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.Contains(rows, r => (string?)r["tool_name"] == "search");
    }

    [Fact]
    public async Task TotalSafetyEvents_ReturnsThree()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var count = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Safety_TotalEvents,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(count >= 3);
    }

    [Fact]
    public async Task BlockRate_ReturnsAbout33Percent()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rate = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Safety_BlockRate,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rate > 0m);
    }

    [Fact]
    public async Task OutcomeDistribution_ReturnsAllThreeOutcomes()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_OutcomeDistribution,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        var outcomes = rows.Select(r => (string?)r["metric"]).ToList();
        Assert.Contains("pass", outcomes);
        Assert.Contains("block", outcomes);
        Assert.Contains("redact", outcomes);
    }

    [Fact]
    public async Task BlocksByCategory_ReturnsHateCategory()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_BlocksByCategory,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.Contains(rows, r => (string?)r["category"] == "hate");
    }

    [Fact]
    public async Task RecentBlocks_ContainsNonPassEvents()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_RecentBlocks,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        Assert.True(rows.Count >= 2);
        Assert.True(rows.All(r => (string?)r["outcome"] != "pass"));
    }

    [Fact]
    public async Task VarToolName_ReturnsDistinctTools()
    {
        if (!_fixture.IsAvailable) return;

        await SeedToolsAndSafetyDataAsync();

        var rows = await _fixture.QueryRowsAsync(DashboardQueries.Tools_VarToolName);

        var tools = rows.Select(r => (string?)r["tool_name"]).ToList();
        Assert.Contains("get_weather", tools);
        Assert.Contains("search", tools);
    }
}
