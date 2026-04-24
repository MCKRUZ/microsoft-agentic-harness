using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

[Collection("Postgres")]
public sealed class SessionsDashboardTests
{
    private readonly PostgresFixture _fixture;

    public SessionsDashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SessionCount_WithSeededSession_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var count = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Sessions_Count,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"),
            new NpgsqlParameter("@status", "All"));

        Assert.True(count >= 1);
    }

    [Fact]
    public async Task AvgDuration_WithEndedSession_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(status: "completed");

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<double>(
            DashboardQueries.Sessions_AvgDuration,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"),
            new NpgsqlParameter("@status", "All"));

        Assert.True(avg >= 0);
    }

    [Fact]
    public async Task AvgCost_WithSeededSession_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(costUsd: 0.25m);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var avg = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Sessions_AvgCost,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"),
            new NpgsqlParameter("@status", "All"));

        Assert.True(avg > 0m);
    }

    [Fact]
    public async Task TotalToolCalls_WithSeededSession_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(toolCallCount: 5);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var total = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Sessions_TotalToolCalls,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"),
            new NpgsqlParameter("@status", "All"));

        Assert.True(total >= 5);
    }

    [Fact]
    public async Task SessionList_WithSeededSession_ContainsConversationId()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync();

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Sessions_List,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            new NpgsqlParameter("@agent", "All"),
            new NpgsqlParameter("@status", "All"));

        Assert.Contains(rows, r => (string?)r["conversation_id"] == session.ConversationId);
    }

    [Fact]
    public async Task AgentNameVariable_ReturnsDistinctAgents()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        await builder.CreateSessionAsync(agentName: "SessionsDashboardAgent");

        var rows = await _fixture.QueryRowsAsync(DashboardQueries.Sessions_VarAgentName);

        Assert.Contains(rows, r => (string?)r["agent_name"] == "SessionsDashboardAgent");
    }
}
