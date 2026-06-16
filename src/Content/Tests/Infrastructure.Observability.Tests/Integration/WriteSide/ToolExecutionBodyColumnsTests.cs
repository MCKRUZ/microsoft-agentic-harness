using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

/// <summary>
/// PR 6 deferreds: verifies the new args / stdout / call_id columns on
/// tool_executions persist correctly through RecordToolExecutionAsync and
/// surface back through the new GetToolExecutionByIdAsync read path.
/// </summary>
[Collection("Postgres")]
public sealed class ToolExecutionBodyColumnsTests
{
    private readonly PostgresFixture _fixture;

    public ToolExecutionBodyColumnsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task RecordToolExecutionAsync_WithBodyColumns_PersistsAllNewFields()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");
        var messageId = await builder.AddMessageAsync(session.Id, role: "assistant", source: "assistant_tool");

        await store.RecordToolExecutionAsync(
            session.Id,
            messageId,
            toolName: "ReadFile",
            toolSource: "keyed_di",
            durationMs: 17,
            status: "success",
            errorType: null,
            resultSize: 1024,
            callId: "call_abc123",
            args: "{\"path\":\"src/app.tsx\"}",
            stdout: "file contents here");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT call_id, args, stdout FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });

        Assert.Single(rows);
        Assert.Equal("call_abc123", rows[0]["call_id"]);
        Assert.Equal("{\"path\":\"src/app.tsx\"}", rows[0]["args"]);
        Assert.Equal("file contents here", rows[0]["stdout"]);
    }

    [SkippableFact]
    public async Task GetToolExecutionByIdAsync_ExistingInvocation_ReturnsFullBody()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id, null,
            toolName: "WriteFile", toolSource: "keyed_di",
            durationMs: 9, status: "success",
            callId: "call_xyz", args: "{\"x\":1}", stdout: "ok");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT id FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        var invocationId = (Guid)rows[0]["id"]!;

        var record = await store.GetToolExecutionByIdAsync(session.Id, invocationId);

        Assert.NotNull(record);
        Assert.Equal("WriteFile", record!.ToolName);
        Assert.Equal("call_xyz", record.CallId);
        Assert.Equal("{\"x\":1}", record.Args);
        Assert.Equal("ok", record.Stdout);
    }

    [SkippableFact]
    public async Task GetToolExecutionByIdAsync_DifferentSession_Returns404()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var ownerSession = await builder.CreateSessionAsync(status: "active");
        var otherSession = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            ownerSession.Id, null,
            toolName: "ReadFile", toolSource: "keyed_di",
            durationMs: 1, status: "success");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT id FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = ownerSession.Id });
        var invocationId = (Guid)rows[0]["id"]!;

        // Lookup scoped to the wrong session must return null — protects against
        // cross-session deep-link traversal.
        var record = await store.GetToolExecutionByIdAsync(otherSession.Id, invocationId);

        Assert.Null(record);
    }

    [SkippableFact]
    public async Task RecordToolExecutionAsync_NullBodyColumns_RoundTripCleanly()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        await store.RecordToolExecutionAsync(
            session.Id, null,
            toolName: "Legacy", toolSource: "keyed_di",
            durationMs: 1, status: "success");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT id FROM tool_executions WHERE session_id = $1",
            new NpgsqlParameter { Value = session.Id });
        var record = await store.GetToolExecutionByIdAsync(session.Id, (Guid)rows[0]["id"]!);

        Assert.NotNull(record);
        Assert.Null(record!.CallId);
        Assert.Null(record.Args);
        Assert.Null(record.Stdout);
    }
}
