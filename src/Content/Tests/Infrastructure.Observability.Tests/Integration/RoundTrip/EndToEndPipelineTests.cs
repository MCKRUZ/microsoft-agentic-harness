using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.RoundTrip;

[Collection("Postgres")]
public class EndToEndPipelineTests
{
    private readonly PostgresFixture _fixture;

    public EndToEndPipelineTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FullChatSession_WriteThenQueryOverview_MatchesSeededMetrics()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(convId, "E2E-Agent", "gpt-4o");
        Assert.NotEqual(Guid.Empty, sessionId);

        await store.UpdateSessionMetricsAsync(
            sessionId, turnCount: 5, toolCallCount: 3, subagentCount: 1,
            totalInputTokens: 2000, totalOutputTokens: 1000,
            totalCacheRead: 400, totalCacheWrite: 200,
            totalCostUsd: 0.50m, cacheHitRate: 0.30m, model: "gpt-4o");

        await store.EndSessionAsync(sessionId, "completed", null);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);
        var timeParams = new[]
        {
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to)
        };

        // Overview: Total Sessions
        var totalSessions = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Overview_TotalSessions, timeParams);
        Assert.True(totalSessions >= 1);

        // Overview: Total Cost
        var totalCost = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Overview_TotalCost, timeParams);
        Assert.True(totalCost >= 0.50m);

        // Overview: Total Tokens
        var totalTokens = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Overview_TotalTokens, timeParams);
        Assert.True(totalTokens >= 3000);

        // Overview: Avg Cache Hit
        var avgCacheHit = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Overview_AvgCacheHit, timeParams);
        Assert.True(avgCacheHit > 0);

        // Overview: Recent Sessions
        var recent = await _fixture.QueryRowsAsync(
            DashboardQueries.Overview_RecentSessions, timeParams);
        Assert.Contains(recent, r => r["conversation_id"]?.ToString() == convId);
    }

    [Fact]
    public async Task SingleSessionWithTools_AppearsInSessionDetail()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(
            agentName: "DetailAgent", model: "claude-sonnet-4-5",
            toolCallCount: 2, costUsd: 0.25m);

        var msgId1 = await builder.AddMessageAsync(session.Id, turnIndex: 0, role: "user",
            source: "user_message", contentPreview: "Hello agent");
        var msgId2 = await builder.AddMessageAsync(session.Id, turnIndex: 1, role: "assistant",
            source: "assistant_tool", model: "claude-sonnet-4-5",
            inputTokens: 200, outputTokens: 150, toolNames: new[] { "get_weather" });
        var msgId3 = await builder.AddMessageAsync(session.Id, turnIndex: 2, role: "assistant",
            source: "assistant_text", model: "claude-sonnet-4-5",
            inputTokens: 300, outputTokens: 200);

        await builder.AddToolAsync(session.Id, "get_weather", "keyed_di", durationMs: 42, messageId: msgId2);
        await builder.AddToolAsync(session.Id, "search_docs", "mcp", durationMs: 150, messageId: msgId2);
        await builder.AddSafetyAsync(session.Id, "prompt", "pass");

        var sessionIdParam = new NpgsqlParameter("@session_id", session.ConversationId);

        // Agent Name
        var agentName = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_AgentName, sessionIdParam);
        Assert.Equal("DetailAgent", agentName);

        // Model
        var model = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_Model, sessionIdParam);
        Assert.Equal("claude-sonnet-4-5", model);

        // Status
        var status = await _fixture.QueryScalarAsync<string>(
            DashboardQueries.Detail_Status, sessionIdParam);
        Assert.Equal("completed", status);

        // Duration
        var duration = await _fixture.QueryScalarAsync<int>(
            DashboardQueries.Detail_Duration, sessionIdParam);
        Assert.True(duration >= 0);

        // Message Timeline
        var messages = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_MessageTimeline, sessionIdParam);
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]["role"]?.ToString());

        // Tool Executions
        var tools = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_ToolExecutions, sessionIdParam);
        Assert.Equal(2, tools.Count);

        // Safety Events
        var safety = await _fixture.QueryRowsAsync(
            DashboardQueries.Detail_SafetyEvents, sessionIdParam);
        Assert.Single(safety);
        Assert.Equal("pass", safety[0]["outcome"]?.ToString());
    }

    [Fact]
    public async Task BlockedPromptFlow_AppearsInSafetyDashboard()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(agentName: "SafetyAgent");

        await builder.AddSafetyAsync(session.Id, "prompt", "block", category: "hate", severity: 4, filterName: "ContentFilter");
        await builder.AddSafetyAsync(session.Id, "response", "redact", category: "pii", severity: 2, filterName: "PiiFilter");
        await builder.AddSafetyAsync(session.Id, "prompt", "pass");

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);
        var timeParams = new[]
        {
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to)
        };

        // Total safety events
        var total = await _fixture.QueryScalarAsync<long>(
            DashboardQueries.Safety_TotalEvents, timeParams);
        Assert.True(total >= 3);

        // Block rate
        var blockRate = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Safety_BlockRate, timeParams);
        Assert.True(blockRate > 0);

        // Outcome distribution
        var outcomes = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_OutcomeDistribution, timeParams);
        Assert.True(outcomes.Count >= 3);

        // Blocks by category
        var categories = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_BlocksByCategory, timeParams);
        Assert.Contains(categories, r => r["category"]?.ToString() == "hate");

        // Recent blocks (non-pass)
        var recentBlocks = await _fixture.QueryRowsAsync(
            DashboardQueries.Safety_RecentBlocks, timeParams);
        Assert.True(recentBlocks.Count >= 2);
    }

    [Fact]
    public async Task ConcurrentSessions_DoNotCrossContaminate()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var agentA = $"AgentA-{_fixture.RunTag[..8]}";
        var agentB = $"AgentB-{_fixture.RunTag[..8]}";
        var agentC = $"AgentC-{_fixture.RunTag[..8]}";

        var tasks = new[]
        {
            builder.CreateSessionAsync(agentName: agentA, costUsd: 1.00m),
            builder.CreateSessionAsync(agentName: agentB, costUsd: 2.00m),
            builder.CreateSessionAsync(agentName: agentC, costUsd: 3.00m)
        };
        var sessions = await Task.WhenAll(tasks);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var costByAgent = await _fixture.QueryRowsAsync(
            DashboardQueries.Cost_ByAgent,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));

        var agentARow = costByAgent.FirstOrDefault(r => r["agent_name"]?.ToString() == agentA);
        var agentBRow = costByAgent.FirstOrDefault(r => r["agent_name"]?.ToString() == agentB);
        var agentCRow = costByAgent.FirstOrDefault(r => r["agent_name"]?.ToString() == agentC);

        Assert.NotNull(agentARow);
        Assert.NotNull(agentBRow);
        Assert.NotNull(agentCRow);

        Assert.Equal(1.00m, Convert.ToDecimal(agentARow!["cost"]));
        Assert.Equal(2.00m, Convert.ToDecimal(agentBRow!["cost"]));
        Assert.Equal(3.00m, Convert.ToDecimal(agentCRow!["cost"]));
    }

    [Fact]
    public async Task FailedTool_AppearsInToolErrorRate()
    {
        if (!_fixture.IsAvailable) return;

        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(agentName: "ToolErrorAgent");

        var toolName = $"test_tool_{_fixture.RunTag[..8]}";
        await builder.AddToolAsync(session.Id, toolName, status: "success", durationMs: 10);
        await builder.AddToolAsync(session.Id, toolName, status: "failure", errorType: "ApiError", durationMs: 500);

        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddMinutes(1);
        var toolParam = new NpgsqlParameter("@tool", "All");

        // Overall error rate should be non-zero
        var errorRate = await _fixture.QueryScalarAsync<decimal>(
            DashboardQueries.Tools_ErrorRate,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to),
            toolParam);
        Assert.True(errorRate > 0);

        // Performance table should show our tool
        var perf = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_PerformanceTable,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));
        var toolRow = perf.FirstOrDefault(r => r["tool_name"]?.ToString() == toolName);
        Assert.NotNull(toolRow);
        Assert.Equal(2L, Convert.ToInt64(toolRow!["calls"]));

        // Recent errors should contain our failed tool
        var errors = await _fixture.QueryRowsAsync(
            DashboardQueries.Tools_RecentErrors,
            new NpgsqlParameter("@from", from),
            new NpgsqlParameter("@to", to));
        Assert.Contains(errors, r =>
            r["tool_name"]?.ToString() == toolName &&
            r["error_type"]?.ToString() == "ApiError");
    }
}
