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

    [Fact]
    public async Task MultiTurnConversation_AccumulatesMetricsAndRecordsAllMessages()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(convId, "MultiTurnAgent", "claude-sonnet-4-6");
        Assert.NotEqual(Guid.Empty, sessionId);

        // Simulate 3 turns, accumulating metrics like the hub does
        var turnData = new[]
        {
            new { InputTokens = 500, OutputTokens = 200, CacheRead = 100, CacheWrite = 50, CostUsd = 0.05m, Tool = (string?)null },
            new { InputTokens = 700, OutputTokens = 350, CacheRead = 200, CacheWrite = 80, CostUsd = 0.08m, Tool = (string?)"code_search" },
            new { InputTokens = 900, OutputTokens = 500, CacheRead = 400, CacheWrite = 100, CostUsd = 0.12m, Tool = (string?)"file_read" },
        };

        int accumInput = 0, accumOutput = 0, accumCacheRead = 0, accumCacheWrite = 0, accumToolCalls = 0;
        decimal accumCost = 0m;

        for (var turn = 0; turn < turnData.Length; turn++)
        {
            var td = turnData[turn];

            // Record user message (handler records this before agent execution)
            await store.RecordMessageAsync(
                sessionId, turn, "user", "user_message",
                $"User question for turn {turn}", null,
                0, 0, 0, 0, 0m, 0m);

            // Record assistant message (handler records this after agent execution)
            var toolNames = td.Tool is not null ? new[] { td.Tool } : null;
            await store.RecordMessageAsync(
                sessionId, turn, "assistant",
                td.Tool is not null ? "assistant_mixed" : "assistant_text",
                $"Assistant response for turn {turn}", "claude-sonnet-4-6",
                td.InputTokens, td.OutputTokens, td.CacheRead, td.CacheWrite,
                td.CostUsd,
                td.InputTokens > 0 ? (decimal)td.CacheRead / td.InputTokens : 0m,
                toolNames);

            // Record tool execution if applicable
            if (td.Tool is not null)
            {
                accumToolCalls++;
                await store.RecordToolExecutionAsync(
                    sessionId, null, td.Tool, "keyed_di",
                    42, "success", null, 256);
            }

            // Accumulate and update session metrics (mirrors hub's pattern)
            accumInput += td.InputTokens;
            accumOutput += td.OutputTokens;
            accumCacheRead += td.CacheRead;
            accumCacheWrite += td.CacheWrite;
            accumCost += td.CostUsd;

            await store.UpdateSessionMetricsAsync(
                sessionId, turnCount: turn + 1, toolCallCount: accumToolCalls, subagentCount: 0,
                accumInput, accumOutput, accumCacheRead, accumCacheWrite,
                accumCost,
                accumInput > 0 ? (decimal)accumCacheRead / accumInput : 0m,
                "claude-sonnet-4-6");
        }

        await store.EndSessionAsync(sessionId, "completed", null);

        // ── Read back via store methods (same path as SessionsController) ──

        var session = await store.GetSessionByIdAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("MultiTurnAgent", session!.AgentName);
        Assert.Equal("completed", session.Status);
        Assert.Equal("claude-sonnet-4-6", session.Model);

        // Aggregate metrics match final accumulation
        Assert.Equal(3, session.TurnCount);
        Assert.Equal(2, session.ToolCallCount);
        Assert.Equal(accumInput, session.TotalInputTokens);
        Assert.Equal(accumOutput, session.TotalOutputTokens);
        Assert.Equal(accumCacheRead, session.TotalCacheRead);
        Assert.Equal(accumCacheWrite, session.TotalCacheWrite);
        Assert.Equal(accumCost, session.TotalCostUsd);
        Assert.True(session.CacheHitRate > 0);
        Assert.NotNull(session.DurationMs);
        Assert.True(session.DurationMs >= 0);

        // All 6 messages present (user + assistant per turn)
        var messages = await store.GetSessionMessagesAsync(sessionId);
        Assert.Equal(6, messages.Count);

        // Messages ordered by turn_index
        var userMessages = messages.Where(m => m.Role == "user").ToList();
        var assistantMessages = messages.Where(m => m.Role == "assistant").ToList();
        Assert.Equal(3, userMessages.Count);
        Assert.Equal(3, assistantMessages.Count);

        // Turn indices are correct
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains(messages, m => m.TurnIndex == i && m.Role == "user");
            Assert.Contains(messages, m => m.TurnIndex == i && m.Role == "assistant");
        }

        // Assistant messages have token data
        var lastAssistant = assistantMessages.Last();
        Assert.True(lastAssistant.InputTokens > 0);
        Assert.True(lastAssistant.OutputTokens > 0);
        Assert.True(lastAssistant.CostUsd > 0);

        // Tool-bearing messages have tool names
        var toolMessages = messages.Where(m => m.ToolNames is { Length: > 0 }).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Contains(toolMessages, m => m.ToolNames!.Contains("code_search"));
        Assert.Contains(toolMessages, m => m.ToolNames!.Contains("file_read"));

        // Tool executions present
        var tools = await store.GetSessionToolExecutionsAsync(sessionId);
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.ToolName == "code_search");
        Assert.Contains(tools, t => t.ToolName == "file_read");
    }

    [Fact]
    public async Task MultiTurnConversation_MetricsMatchDashboardQueries()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(convId, "DashboardQueryAgent", "claude-sonnet-4-6");
        Assert.NotEqual(Guid.Empty, sessionId);

        // 2-turn session with known values
        await store.RecordMessageAsync(sessionId, 0, "user", "user_message", "Hello", null, 0, 0, 0, 0, 0m, 0m);
        await store.RecordMessageAsync(sessionId, 0, "assistant", "assistant_text", "Hi there",
            "claude-sonnet-4-6", 300, 150, 60, 30, 0.03m, 0.20m);
        await store.UpdateSessionMetricsAsync(sessionId, 1, 0, 0, 300, 150, 60, 30, 0.03m, 0.20m, "claude-sonnet-4-6");

        await store.RecordMessageAsync(sessionId, 1, "user", "user_message", "Tell me more", null, 0, 0, 0, 0, 0m, 0m);
        await store.RecordMessageAsync(sessionId, 1, "assistant", "assistant_mixed", "Here's detail",
            "claude-sonnet-4-6", 600, 300, 150, 50, 0.07m, 0.25m, new[] { "web_search" });
        await store.RecordToolExecutionAsync(sessionId, null, "web_search", "mcp", 200, "success", null, 1024);
        await store.UpdateSessionMetricsAsync(sessionId, 2, 1, 0, 900, 450, 210, 80, 0.10m, 0.23m, "claude-sonnet-4-6");

        await store.EndSessionAsync(sessionId, "completed", null);

        var sessionIdParam = new NpgsqlParameter("@session_id", convId);

        // Dashboard queries return correct values
        var agentName = await _fixture.QueryScalarAsync<string>(DashboardQueries.Detail_AgentName, sessionIdParam);
        Assert.Equal("DashboardQueryAgent", agentName);

        var totalTokens = await _fixture.QueryScalarAsync<int>(DashboardQueries.Detail_TotalTokens, sessionIdParam);
        Assert.Equal(1350, totalTokens); // 900 in + 450 out

        var totalCost = await _fixture.QueryScalarAsync<decimal>(DashboardQueries.Detail_TotalCost, sessionIdParam);
        Assert.Equal(0.10m, totalCost);

        var toolCalls = await _fixture.QueryScalarAsync<int>(DashboardQueries.Detail_ToolCalls, sessionIdParam);
        Assert.Equal(1, toolCalls);

        var cacheHit = await _fixture.QueryScalarAsync<decimal>(DashboardQueries.Detail_CacheHitRate, sessionIdParam);
        Assert.Equal(0.23m, cacheHit);

        var duration = await _fixture.QueryScalarAsync<int>(DashboardQueries.Detail_Duration, sessionIdParam);
        Assert.True(duration >= 0);

        // Message timeline returns all 4 messages in order
        var msgRows = await _fixture.QueryRowsAsync(DashboardQueries.Detail_MessageTimeline, sessionIdParam);
        Assert.Equal(4, msgRows.Count);
        Assert.Equal("user", msgRows[0]["role"]?.ToString());
        Assert.Equal("assistant", msgRows[1]["role"]?.ToString());
        Assert.Equal("user", msgRows[2]["role"]?.ToString());
        Assert.Equal("assistant", msgRows[3]["role"]?.ToString());

        // Tool executions
        var toolRows = await _fixture.QueryRowsAsync(DashboardQueries.Detail_ToolExecutions, sessionIdParam);
        Assert.Single(toolRows);
        Assert.Equal("web_search", toolRows[0]["tool_name"]?.ToString());
    }
}
