using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

[Collection("Postgres")]
public sealed class SessionWriteTests
{
    private readonly PostgresFixture _fixture;

    public SessionWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StartSessionAsync_NewConversation_InsertsRowAndReturnsId()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentA", "gpt-4o");

        Assert.NotEqual(Guid.Empty, sessionId);

        var count = await _fixture.QueryScalarAsync<long>(
            "SELECT COUNT(*) FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Equal(1, count);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT conversation_id, agent_name FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Single(rows);
        Assert.Equal(conversationId, rows[0]["conversation_id"]);
        Assert.Equal("AgentA", rows[0]["agent_name"]);
    }

    [Fact]
    public async Task StartSessionAsync_WithModel_PersistsModel()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentB", "claude-3-5-sonnet");

        var model = await _fixture.QueryScalarAsync<string>(
            "SELECT model FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Equal("claude-3-5-sonnet", model);
    }

    [Fact]
    public async Task StartSessionAsync_NullModel_AllowsNull()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentC", null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT model FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Single(rows);
        Assert.Null(rows[0]["model"]);
    }

    [Fact]
    public async Task StartSessionAsync_DuplicateConversationId_UpdatesStartedAt()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var firstId = await store.StartSessionAsync(conversationId, "AgentX", "gpt-4o");
        var firstStart = await _fixture.QueryScalarAsync<DateTime>(
            "SELECT started_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = firstId });

        await Task.Delay(50);

        var secondId = await store.StartSessionAsync(conversationId, "AgentX", "gpt-4o");
        var secondStart = await _fixture.QueryScalarAsync<DateTime>(
            "SELECT started_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = secondId });

        Assert.Equal(firstId, secondId);
        Assert.True(secondStart >= firstStart);

        var count = await _fixture.QueryScalarAsync<long>(
            "SELECT COUNT(*) FROM sessions WHERE conversation_id = $1",
            new NpgsqlParameter { Value = conversationId });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EndSessionAsync_SetsStatusAndEndedAt()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentE", "gpt-4o");
        await Task.Delay(10);
        await store.EndSessionAsync(sessionId, "completed", null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, ended_at, duration_ms FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        Assert.Equal("completed", rows[0]["status"]);
        Assert.NotNull(rows[0]["ended_at"]);
        Assert.NotNull(rows[0]["duration_ms"]);
        Assert.True(Convert.ToInt64(rows[0]["duration_ms"]) >= 0);
    }

    [Fact]
    public async Task EndSessionAsync_EmptyGuid_NoOp()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var ex = await Record.ExceptionAsync(() => store.EndSessionAsync(Guid.Empty, "completed", null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task UpdateSessionMetricsAsync_PersistsAllFields()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentM", "gpt-4o");
        await store.UpdateSessionMetricsAsync(
            sessionId,
            turnCount: 7,
            toolCallCount: 5,
            subagentCount: 2,
            totalInputTokens: 1234,
            totalOutputTokens: 567,
            totalCacheRead: 321,
            totalCacheWrite: 111,
            totalCostUsd: 0.4242m,
            cacheHitRate: 0.75m,
            model: "gpt-4o-mini");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT turn_count, tool_call_count, subagent_count, total_input_tokens, total_output_tokens, " +
            "total_cache_read, total_cache_write, total_cost_usd, cache_hit_rate, model " +
            "FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(7, Convert.ToInt32(row["turn_count"]));
        Assert.Equal(5, Convert.ToInt32(row["tool_call_count"]));
        Assert.Equal(2, Convert.ToInt32(row["subagent_count"]));
        Assert.Equal(1234, Convert.ToInt32(row["total_input_tokens"]));
        Assert.Equal(567, Convert.ToInt32(row["total_output_tokens"]));
        Assert.Equal(321, Convert.ToInt32(row["total_cache_read"]));
        Assert.Equal(111, Convert.ToInt32(row["total_cache_write"]));
        Assert.Equal(0.4242m, Convert.ToDecimal(row["total_cost_usd"]));
        Assert.Equal(0.75m, Convert.ToDecimal(row["cache_hit_rate"]));
        Assert.Equal("gpt-4o-mini", row["model"]);
    }
}
