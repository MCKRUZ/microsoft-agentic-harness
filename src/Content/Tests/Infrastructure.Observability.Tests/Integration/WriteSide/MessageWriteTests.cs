using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

[Collection("Postgres")]
public sealed class MessageWriteTests
{
    private readonly PostgresFixture _fixture;

    public MessageWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecordMessageAsync_UserMessage_PersistsAllFields()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var messageId = await store.RecordMessageAsync(
            session.Id,
            turnIndex: 2,
            role: "user",
            source: "user_message",
            contentPreview: "hello world",
            model: "gpt-4o",
            inputTokens: 10,
            outputTokens: 0,
            cacheRead: 0,
            cacheWrite: 0,
            costUsd: 0.001m,
            cacheHitPct: 0m,
            toolNames: null);

        Assert.NotEqual(Guid.Empty, messageId);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT session_id, turn_index, role, source, content_preview, model, " +
            "input_tokens, output_tokens, cache_read, cache_write, cost_usd, cache_hit_pct " +
            "FROM session_messages WHERE id = $1",
            new NpgsqlParameter { Value = messageId });

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(session.Id, row["session_id"]);
        Assert.Equal(2, Convert.ToInt32(row["turn_index"]));
        Assert.Equal("user", row["role"]);
        Assert.Equal("user_message", row["source"]);
        Assert.Equal("hello world", row["content_preview"]);
        Assert.Equal("gpt-4o", row["model"]);
        Assert.Equal(10, Convert.ToInt32(row["input_tokens"]));
        Assert.Equal(0.001m, Convert.ToDecimal(row["cost_usd"]));
    }

    [Fact]
    public async Task RecordMessageAsync_AssistantWithToolNames_PersistsArray()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var toolNames = new[] { "get_weather", "web_search", "file_read" };

        var messageId = await store.RecordMessageAsync(
            session.Id,
            turnIndex: 1,
            role: "assistant",
            source: "assistant_tool",
            contentPreview: "calling tools",
            model: "gpt-4o",
            inputTokens: 50,
            outputTokens: 20,
            cacheRead: 0,
            cacheWrite: 0,
            costUsd: 0.01m,
            cacheHitPct: 0m,
            toolNames: toolNames);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT tool_names FROM session_messages WHERE id = $1",
            new NpgsqlParameter { Value = messageId });

        Assert.Single(rows);
        var persisted = Assert.IsType<string[]>(rows[0]["tool_names"]);
        Assert.Equal(toolNames, persisted);
    }

    [Fact]
    public async Task RecordMessageAsync_NullOptionalFields_Allowed()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var messageId = await store.RecordMessageAsync(
            session.Id,
            turnIndex: 0,
            role: "system",
            source: null,
            contentPreview: null,
            model: null,
            inputTokens: 0,
            outputTokens: 0,
            cacheRead: 0,
            cacheWrite: 0,
            costUsd: 0m,
            cacheHitPct: 0m,
            toolNames: null);

        Assert.NotEqual(Guid.Empty, messageId);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT source, content_preview, model, tool_names FROM session_messages WHERE id = $1",
            new NpgsqlParameter { Value = messageId });
        Assert.Single(rows);
        Assert.Null(rows[0]["source"]);
        Assert.Null(rows[0]["content_preview"]);
        Assert.Null(rows[0]["model"]);
        Assert.Null(rows[0]["tool_names"]);
    }

    [Fact]
    public async Task RecordMessageAsync_EmptySessionId_ReturnsEmpty()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var messageId = await store.RecordMessageAsync(
            Guid.Empty,
            turnIndex: 0,
            role: "user",
            source: "user_message",
            contentPreview: "x",
            model: "gpt-4o",
            inputTokens: 1,
            outputTokens: 1,
            cacheRead: 0,
            cacheWrite: 0,
            costUsd: 0m,
            cacheHitPct: 0m,
            toolNames: null);

        Assert.Equal(Guid.Empty, messageId);
    }

    [Theory]
    [InlineData("user", "user_message")]
    [InlineData("assistant", "assistant_text")]
    [InlineData("assistant", "assistant_tool")]
    [InlineData("assistant", "assistant_mixed")]
    [InlineData("tool", "tool_result")]
    [InlineData("system", "system_context")]
    [InlineData("system", "hook_injection")]
    public async Task RecordMessageAsync_AllRolesAndSources_CheckConstraintsPassed(string role, string source)
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var messageId = await store.RecordMessageAsync(
            session.Id,
            turnIndex: 0,
            role: role,
            source: source,
            contentPreview: "test",
            model: "gpt-4o",
            inputTokens: 1,
            outputTokens: 1,
            cacheRead: 0,
            cacheWrite: 0,
            costUsd: 0m,
            cacheHitPct: 0m,
            toolNames: null);

        Assert.NotEqual(Guid.Empty, messageId);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT role, source FROM session_messages WHERE id = $1",
            new NpgsqlParameter { Value = messageId });
        Assert.Single(rows);
        Assert.Equal(role, rows[0]["role"]);
        Assert.Equal(source, rows[0]["source"]);
    }
}
