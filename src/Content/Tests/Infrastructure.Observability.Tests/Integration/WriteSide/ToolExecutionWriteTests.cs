using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

[Collection("Postgres")]
public sealed class ToolExecutionWriteTests
{
    private readonly PostgresFixture _fixture;

    public ToolExecutionWriteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecordToolExecutionAsync_Success_PersistsAllFields()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");
        var messageId = await builder.AddMessageAsync(session.Id, role: "assistant", source: "assistant_tool");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId,
            toolName: "get_weather",
            toolSource: "keyed_di",
            durationMs: 123,
            status: "success",
            errorType: null,
            resultSize: 1024);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT session_id, message_id, tool_name, tool_source, duration_ms, status, error_type, result_size " +
            "FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(session.Id, row["session_id"]);
        Assert.Equal(messageId, row["message_id"]);
        Assert.Equal("get_weather", row["tool_name"]);
        Assert.Equal("keyed_di", row["tool_source"]);
        Assert.Equal(123, Convert.ToInt32(row["duration_ms"]));
        Assert.Equal("success", row["status"]);
        Assert.Null(row["error_type"]);
        Assert.Equal(1024, Convert.ToInt32(row["result_size"]));
    }

    [Fact]
    public async Task RecordToolExecutionAsync_Failure_PersistsErrorType()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId: null,
            toolName: "broken_tool",
            toolSource: "mcp",
            durationMs: 42,
            status: "failure",
            errorType: "ArgumentException",
            resultSize: null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, error_type, tool_source FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });

        Assert.Single(rows);
        Assert.Equal("failure", rows[0]["status"]);
        Assert.Equal("ArgumentException", rows[0]["error_type"]);
        Assert.Equal("mcp", rows[0]["tool_source"]);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("timeout")]
    public async Task RecordToolExecutionAsync_AllStatuses_CheckConstraintsPassed(string status)
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId: null,
            toolName: "t",
            toolSource: "keyed_di",
            durationMs: 1,
            status: status,
            errorType: status == "success" ? null : "Err",
            resultSize: 10);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status FROM tool_executions WHERE session_id = $1 AND status = $2",
            new NpgsqlParameter { Value = session.Id },
            new NpgsqlParameter { Value = status });
        Assert.Single(rows);
    }

    [Fact]
    public async Task RecordToolExecutionAsync_NullMessageId_Allowed()
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId: null,
            toolName: "orphan_tool",
            toolSource: "keyed_di",
            durationMs: 5,
            status: "success",
            errorType: null,
            resultSize: 0);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT message_id FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        Assert.Single(rows);
        Assert.Null(rows[0]["message_id"]);
    }

    [Theory]
    [InlineData("keyed_di")]
    [InlineData("mcp")]
    [InlineData("semantic_kernel")]
    public async Task RecordToolExecutionAsync_AllSources_CheckConstraintsPassed(string toolSource)
    {
        if (!_fixture.IsAvailable) return;

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId: null,
            toolName: $"tool_{toolSource}",
            toolSource: toolSource,
            durationMs: 1,
            status: "success",
            errorType: null,
            resultSize: 1);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT tool_source FROM tool_executions WHERE session_id = $1 AND tool_source = $2",
            new NpgsqlParameter { Value = session.Id },
            new NpgsqlParameter { Value = toolSource });
        Assert.Single(rows);
    }
}
