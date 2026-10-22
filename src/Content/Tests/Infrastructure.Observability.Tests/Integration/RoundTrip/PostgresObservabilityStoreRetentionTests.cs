using FluentAssertions;
using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.RoundTrip;

/// <summary>
/// Retention (#901) for <see cref="PostgresObservabilityStore"/>: a session with no activity since the cutoff goes with
/// everything under it, while one with any recent activity stays whatever its start time says, and a conversation whose
/// session was swept is told so when it comes back.
/// </summary>
/// <remarks>
/// The database is shared with the rest of the collection, so every seeded row is dated decades back and every cutoff
/// sits just after it: rows any other test writes now are never in range. Skipped when no Postgres is available.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresObservabilityStoreRetentionTests
{
    private static readonly DateTimeOffset Ancient = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public PostgresObservabilityStoreRetentionTests(PostgresFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AnInactiveSession_GoesWithEverythingUnderIt_AndAnyRecentActivityKeepsOne()
    {
        _fixture.SkipIfUnavailable();
        var idle = _fixture.NewConversationId();
        var resumed = _fixture.NewConversationId();
        var reopened = _fixture.NewConversationId();
        try
        {
            var idleSession = await SeedSessionAsync(idle, endedAt: Ancient.AddDays(1), childAt: Ancient);
            // Started long ago and never ended, but written to just now: a resumed conversation keeps its first start.
            var resumedSession = await SeedSessionAsync(resumed, endedAt: null, childAt: DateTimeOffset.UtcNow);
            // Started long ago and ended just now.
            await SeedSessionAsync(reopened, endedAt: DateTimeOffset.UtcNow, childAt: Ancient);

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            var removed = await store.DeleteInactiveSinceAsync(Cutoff, 100, CancellationToken.None);

            removed.Should().BeGreaterThanOrEqualTo(1);
            (await SessionsAsync(idle)).Should().Be(0);
            (await MessagesAsync(idleSession)).Should().Be(0, "a session's messages go with it");
            (await AuditRowsAsync(idle)).Should().Be(0, "audit rows go first, or they would outlive their session unlinked");
            (await SessionsAsync(resumed)).Should().Be(1);
            (await MessagesAsync(resumedSession)).Should().Be(1);
            (await SessionsAsync(reopened)).Should().Be(1);
        }
        finally
        {
            await CleanUpAsync(idle, resumed, reopened);
        }
    }

    [SkippableFact]
    public async Task ASessionReopenedJustNow_IsKept_BeforeItsFirstTurnLands()
    {
        // Resuming clears ended_at and keeps the first start, so without a recorded activity time a conversation reopened
        // after a long gap read as idle until its first turn, and a sweep in between removed the session it had adopted.
        _fixture.SkipIfUnavailable();
        var conversation = _fixture.NewConversationId();
        try
        {
            var session = await SeedSessionAsync(conversation, endedAt: Ancient.AddDays(1), childAt: Ancient);
            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            (await store.ResumeSessionAsync(session)).Should().BeTrue();

            await store.DeleteInactiveSinceAsync(Cutoff, 100, CancellationToken.None);

            (await SessionsAsync(conversation)).Should().Be(1);
        }
        finally
        {
            await CleanUpAsync(conversation);
        }
    }

    [SkippableFact]
    public async Task SnapshotsAndSessionlessAuditRows_GoByTheirOwnAge()
    {
        _fixture.SkipIfUnavailable();
        var conversation = _fixture.NewConversationId();
        try
        {
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshots (conversation_id, turn_index, turn_id, captured_at) VALUES ($1, 0, 't-0', $2), ($1, 1, 't-1', NOW())",
                new NpgsqlParameter { Value = conversation }, new NpgsqlParameter { Value = Ancient });
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshot_loaded_bodies (conversation_id, turn_index, loaded_index, body, captured_at) VALUES ($1, 0, 0, 'old', $2), ($1, 1, 0, 'new', NOW())",
                new NpgsqlParameter { Value = conversation }, new NpgsqlParameter { Value = Ancient });
            await InsertAuditAsync(sessionId: null, conversation, Ancient);
            await InsertAuditAsync(sessionId: null, conversation, DateTimeOffset.UtcNow);

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            await store.DeleteInactiveSinceAsync(Cutoff, 100, CancellationToken.None);

            (await CountAsync("SELECT count(*) FROM context_snapshots WHERE conversation_id = $1", conversation)).Should().Be(1);
            (await CountAsync("SELECT count(*) FROM context_snapshot_loaded_bodies WHERE conversation_id = $1", conversation)).Should().Be(1);
            (await AuditRowsAsync(conversation)).Should().Be(1);
        }
        finally
        {
            await CleanUpAsync(conversation);
        }
    }

    [SkippableFact]
    public async Task OneSweep_RemovesNoMoreSessionsThanItsCap_OldestFirst()
    {
        _fixture.SkipIfUnavailable();
        var older = _fixture.NewConversationId();
        var newer = _fixture.NewConversationId();
        var start = new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await SeedSessionAsync(older, endedAt: null, childAt: start, startedAt: start);
            await SeedSessionAsync(newer, endedAt: null, childAt: start.AddDays(1), startedAt: start.AddDays(1));

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            var removed = await store.DeleteInactiveSinceAsync(start.AddDays(30), 1, CancellationToken.None);

            removed.Should().Be(1);
            (await SessionsAsync(older)).Should().Be(0);
            (await SessionsAsync(newer)).Should().Be(1);
        }
        finally
        {
            await CleanUpAsync(older, newer);
        }
    }

    [SkippableFact]
    public async Task ResumingASessionThatWasSwept_SaysItIsGone()
    {
        _fixture.SkipIfUnavailable();
        var conversation = _fixture.NewConversationId();
        try
        {
            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            var session = await store.StartSessionAsync(conversation, "retention-agent", model: null);
            (await store.ResumeSessionAsync(session)).Should().BeTrue();

            await _fixture.ExecuteAsync("DELETE FROM sessions WHERE id = $1", new NpgsqlParameter { Value = session });

            (await store.ResumeSessionAsync(session)).Should().BeFalse();
        }
        finally
        {
            await CleanUpAsync(conversation);
        }
    }

    private async Task<Guid> SeedSessionAsync(
        string conversationId, DateTimeOffset? endedAt, DateTimeOffset childAt, DateTimeOffset? startedAt = null)
    {
        var sessionId = Guid.NewGuid();
        await _fixture.ExecuteAsync(
            "INSERT INTO sessions (id, conversation_id, agent_name, started_at, ended_at, last_activity_at) "
            + "VALUES ($1, $2, 'retention-agent', $3, $4, GREATEST($3, COALESCE($4, $3)))",
            new NpgsqlParameter { Value = sessionId },
            new NpgsqlParameter { Value = conversationId },
            new NpgsqlParameter { Value = startedAt ?? Ancient },
            new NpgsqlParameter { Value = (object?)endedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await _fixture.ExecuteAsync(
            "INSERT INTO session_messages (session_id, turn_index, role, content_full, created_at) VALUES ($1, 0, 'user', $2, $3)",
            new NpgsqlParameter { Value = sessionId },
            new NpgsqlParameter { Value = conversationId },
            new NpgsqlParameter { Value = childAt });
        await InsertAuditAsync(sessionId, conversationId, childAt);
        return sessionId;
    }

    private Task InsertAuditAsync(Guid? sessionId, string conversationId, DateTimeOffset createdAt) =>
        _fixture.ExecuteAsync(
            "INSERT INTO audit_log (operation, source, session_id, metadata, created_at) "
            + "VALUES ('retention-test', 'harness', $1, jsonb_build_object('run_tag', $2::text, 'conversation_id', $3::text), $4)",
            new NpgsqlParameter { Value = (object?)sessionId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            new NpgsqlParameter { Value = _fixture.RunTag },
            new NpgsqlParameter { Value = conversationId },
            new NpgsqlParameter { Value = createdAt });

    private Task<int> SessionsAsync(string conversationId) =>
        CountAsync("SELECT count(*) FROM sessions WHERE conversation_id = $1", conversationId);

    private Task<int> AuditRowsAsync(string conversationId) =>
        CountAsync("SELECT count(*) FROM audit_log WHERE metadata->>'conversation_id' = $1", conversationId);

    private async Task<int> MessagesAsync(Guid sessionId) =>
        await _fixture.QueryScalarAsync<int>(
            "SELECT count(*) FROM session_messages WHERE session_id = $1", new NpgsqlParameter { Value = sessionId });

    private async Task<int> CountAsync(string sql, string conversationId) =>
        await _fixture.QueryScalarAsync<int>(sql, new NpgsqlParameter { Value = conversationId });

    /// <summary>Removes whatever a test left, in every table, by the conversation ids it used.</summary>
    private async Task CleanUpAsync(params string[] conversationIds)
    {
        var ids = new NpgsqlParameter { Value = conversationIds };
        await _fixture.ExecuteAsync("DELETE FROM audit_log WHERE metadata->>'conversation_id' = ANY($1)", ids);
        await _fixture.ExecuteAsync("DELETE FROM context_snapshot_loaded_bodies WHERE conversation_id = ANY($1)", new NpgsqlParameter { Value = conversationIds });
        await _fixture.ExecuteAsync("DELETE FROM context_snapshots WHERE conversation_id = ANY($1)", new NpgsqlParameter { Value = conversationIds });
        await _fixture.ExecuteAsync("DELETE FROM sessions WHERE conversation_id = ANY($1)", new NpgsqlParameter { Value = conversationIds });
    }
}
