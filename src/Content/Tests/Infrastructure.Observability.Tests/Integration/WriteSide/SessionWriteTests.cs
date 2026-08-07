using Domain.AI.Observability.Models;
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

    [SkippableFact]
    public async Task StartSessionAsync_NewConversation_InsertsRowAndReturnsId()
    {
        _fixture.SkipIfUnavailable();

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

    [SkippableFact]
    public async Task StartSessionAsync_WithModel_PersistsModel()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentB", "claude-3-5-sonnet");

        var model = await _fixture.QueryScalarAsync<string>(
            "SELECT model FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Equal("claude-3-5-sonnet", model);
    }

    [SkippableFact]
    public async Task StartSessionAsync_NullModel_AllowsNull()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentC", null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT model FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });
        Assert.Single(rows);
        Assert.Null(rows[0]["model"]);
    }

    [SkippableFact]
    public async Task StartSessionAsync_DuplicateConversationId_UpdatesStartedAt()
    {
        _fixture.SkipIfUnavailable();

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

    [SkippableFact]
    public async Task StartSessionAsync_ReopeningAnEndedConversation_ClearsTheEndMarkers()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentReopen", "gpt-4o");

        // Spend something first, or the totals below would read zero whether they are reset or not.
        await store.UpdateSessionMetricsAsync(
            sessionId,
            turnCount: 4,
            toolCallCount: 2,
            subagentCount: 0,
            totalInputTokens: 900,
            totalOutputTokens: 300,
            totalCacheRead: 100,
            totalCacheWrite: 50,
            totalCostUsd: 0.1234m,
            cacheHitRate: 0.5m);

        await store.EndSessionAsync(sessionId, SessionStatus.Error, "something broke");

        // Same conversation, opened again — reached when the conversation record has lost its session id
        // (the write after the first open failed, or the record was rebuilt) and the recorder therefore
        // has nothing to adopt. Restamping the start of a row that still says the conversation finished
        // leaves a session that is simultaneously starting and over.
        var reopened = await store.StartSessionAsync(conversationId, "AgentReopen", "gpt-4o");

        Assert.Equal(sessionId, reopened);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, ended_at, error_message, turn_count, total_input_tokens, total_cost_usd " +
            "FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        Assert.Equal("active", rows[0]["status"]);
        Assert.Null(rows[0]["ended_at"]);
        Assert.Null(rows[0]["error_message"]);

        // The totals restart with the session. The recorder reached here because it had nothing to
        // adopt, so its own accumulator is at zero — a row still carrying the previous session's spend
        // would credit the new one with tokens it has not used.
        Assert.Equal(0, Convert.ToInt32(rows[0]["turn_count"]));
        Assert.Equal(0, Convert.ToInt32(rows[0]["total_input_tokens"]));
        Assert.Equal(0m, Convert.ToDecimal(rows[0]["total_cost_usd"]));
    }

    [SkippableFact]
    public async Task EndSessionAsync_SetsStatusAndEndedAt()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentE", "gpt-4o");
        await Task.Delay(10);
        await store.EndSessionAsync(sessionId, SessionStatus.Completed, null);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, ended_at, duration_ms FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        Assert.Equal("completed", rows[0]["status"]);
        Assert.NotNull(rows[0]["ended_at"]);
        Assert.NotNull(rows[0]["duration_ms"]);
        Assert.True(Convert.ToInt64(rows[0]["duration_ms"]) >= 0);
    }

    public static TheoryData<SessionStatus> AllStatuses()
    {
        var data = new TheoryData<SessionStatus>();
        foreach (var status in Enum.GetValues<SessionStatus>())
            data.Add(status);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(AllStatuses))]
    public async Task EndSessionAsync_EveryStatusTheCodeCanExpress_IsAcceptedByTheSchema(SessionStatus status)
    {
        _fixture.SkipIfUnavailable();

        // The test that was missing. sessions.status is guarded by a CHECK constraint, and two callers
        // were passing words outside it — "errored" from the hub and "cancelled" from a self-contained
        // run. Postgres rejected both updates and the store, which must never fail a turn to report an
        // accounting problem, logged and swallowed the rejection: those sessions simply never ended.
        // Every unit test covering them passed, because a mocked store accepts any string at all.
        //
        // Driven off Enum.GetValues rather than a hand-written list, so a member added to SessionStatus
        // without a matching schema literal fails here instead of in production.
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentStatus", "gpt-4o");
        await store.EndSessionAsync(sessionId, status, "reason");

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, ended_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        Assert.Equal(status.ToDbValue(), rows[0]["status"]);
        Assert.NotNull(rows[0]["ended_at"]);
    }

    [SkippableFact]
    public async Task ResumeSessionAsync_EndedSession_ClearsTheEndMarkersWithoutRestampingTheStart()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentResume", "gpt-4o");
        var startedAt = await _fixture.QueryScalarAsync<DateTime>(
            "SELECT started_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        await store.EndSessionAsync(sessionId, SessionStatus.Completed, "done");
        await Task.Delay(20);
        await store.ResumeSessionAsync(sessionId);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT status, ended_at, error_message, duration_ms, started_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Single(rows);
        Assert.Equal("active", rows[0]["status"]);
        Assert.Null(rows[0]["ended_at"]);
        Assert.Null(rows[0]["error_message"]);

        // The start must survive. Restamping it is the defect issue #255 fixed: every duration derived
        // from the row would then describe only the stretch since the conversation was last picked up.
        Assert.Equal(startedAt, rows[0]["started_at"]);

        // And a live session still reports how long it has been running, rather than nothing at all
        // until the next turn lands — and permanently if that write fails.
        Assert.NotNull(rows[0]["duration_ms"]);
    }

    [SkippableFact]
    public async Task ResumeSessionAsync_SessionThatWasNeverEnded_LeavesTheRowExactlyAsItWas()
    {
        _fixture.SkipIfUnavailable();

        // This is the ordinary case, not an edge case: the recorder calls resume on every turn that
        // adopts a session, and almost none of those sessions were ever ended. The WHERE clause is what
        // lets it do that without reading the row first, so what has to be proved is that the statement
        // touches nothing — not merely that the columns it would have set already held those values,
        // which stays true whether the guard is there or not.
        //
        // xmin is the id of the transaction that produced the current row version, so it changes if and
        // only if the row was actually written. Comparing the visible columns cannot tell a no-op from a
        // write that happened to set them to what they already were.
        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var conversationId = _fixture.NewConversationId();

        var sessionId = await store.StartSessionAsync(conversationId, "AgentLive", "gpt-4o");
        var before = await _fixture.QueryRowsAsync(
            "SELECT xmin::text AS row_version, status, started_at, ended_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        await Task.Delay(20);
        await store.ResumeSessionAsync(sessionId);

        var after = await _fixture.QueryRowsAsync(
            "SELECT xmin::text AS row_version, status, started_at, ended_at FROM sessions WHERE id = $1",
            new NpgsqlParameter { Value = sessionId });

        Assert.Equal(before[0]["row_version"], after[0]["row_version"]);
        Assert.Equal(before[0]["status"], after[0]["status"]);
        Assert.Equal(before[0]["started_at"], after[0]["started_at"]);
        Assert.Null(after[0]["ended_at"]);
    }

    [SkippableFact]
    public async Task ResumeSessionAsync_EmptyGuid_NoOp()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var ex = await Record.ExceptionAsync(() => store.ResumeSessionAsync(Guid.Empty));
        Assert.Null(ex);
    }

    [SkippableFact]
    public async Task EndSessionAsync_EmptyGuid_NoOp()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var ex = await Record.ExceptionAsync(() => store.EndSessionAsync(Guid.Empty, SessionStatus.Completed, null));
        Assert.Null(ex);
    }

    [SkippableFact]
    public async Task UpdateSessionMetricsAsync_PersistsAllFields()
    {
        _fixture.SkipIfUnavailable();

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
