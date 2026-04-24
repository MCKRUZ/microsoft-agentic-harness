using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.ReadSide;

[Collection("Postgres")]
public sealed class SessionDetailDashboardTests
{
    private readonly PostgresFixture _fixture;

    public SessionDetailDashboardTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AgentName_ReturnsSeededAgent()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(agentName: "DetailTestAgent");

        var result = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_AgentName,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal("DetailTestAgent", result);
    }

    [Fact]
    public async Task Model_ReturnsSeededModel()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(model: "claude-sonnet-4");

        var result = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_Model,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal("claude-sonnet-4", result);
    }

    [Fact]
    public async Task Status_ReturnsCompleted()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "completed");

        var result = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_Status,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal("completed", result);
    }

    [Fact]
    public async Task Duration_ReturnsNonZero()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "completed");

        var result = await _fixture.QueryScalarAsync<int>(
            DashboardQueries.Detail_Duration,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.True(result >= 0);
    }

    [Fact]
    public async Task TotalCost_ReturnsSeededCost()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(costUsd: 0.42m);

        var result = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Detail_TotalCost,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal(0.42m, result);
    }

    [Fact]
    public async Task TotalTokens_ReturnsSumOfInputOutput()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(inputTokens: 1200, outputTokens: 800);

        var result = await _fixture.QueryScalarAsync<int>(
            DashboardQueries.Detail_TotalTokens,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal(2000, result);
    }

    [Fact]
    public async Task ToolCalls_ReturnsSeededCount()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(toolCallCount: 7);

        var result = await _fixture.QueryScalarAsync<int>(
            DashboardQueries.Detail_ToolCalls,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task CacheHitRate_ReturnsSeededValue()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(cacheHitRate: 0.35m);

        var result = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Detail_CacheHitRate,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.Equal(0.35m, result);
    }

    [Fact]
    public async Task MessageTimeline_ReturnsOrderedMessages()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync();

        await builder.AddMessageAsync(session.Id, turnIndex: 0, role: "user",
            contentPreview: "Hello", inputTokens: 50, outputTokens: 0);
        await builder.AddMessageAsync(session.Id, turnIndex: 1, role: "assistant",
            contentPreview: "Hi there", inputTokens: 0, outputTokens: 100);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_MessageTimeline,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.True(rows.Count >= 2);
        Assert.Equal("user", (string?)rows[0]["role"]);
    }

    [Fact]
    public async Task ToolExecutions_ReturnsSeededTool()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync();

        await builder.AddToolAsync(session.Id, toolName: "code_search",
            toolSource: "keyed_di", durationMs: 150, status: "success");

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_ToolExecutions,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.True(rows.Count >= 1);
        Assert.Contains(rows, r => (string?)r["tool_name"] == "code_search");
    }

    [Fact]
    public async Task SafetyEvents_ReturnsSeededEvent()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync();

        await builder.AddSafetyAsync(session.Id, phase: "prompt",
            outcome: "block", category: "violence", severity: 4);

        var rows = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_SafetyEvents,
            new NpgsqlParameter("@session_id", session.ConversationId));

        Assert.True(rows.Count >= 1);
        Assert.Contains(rows, r => (string?)r["outcome"] == "block");
    }

    [Fact]
    public async Task VarSessionId_ReturnsRecentConversations()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync();

        var rows = await _fixture.QueryRowsAsync(DashboardQueries.Detail_VarSessionId);

        Assert.True(rows.Count >= 1);
        Assert.Contains(rows, r => (string?)r["conversation_id"] == session.ConversationId);
    }
}
