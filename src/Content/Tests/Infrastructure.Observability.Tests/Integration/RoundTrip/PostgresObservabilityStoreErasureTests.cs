using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.RoundTrip;

/// <summary>
/// Right-to-erasure (#900) for <see cref="PostgresObservabilityStore"/>: everything recorded for the person's
/// conversations — sessions and the messages cascading from them, audit rows, context snapshots and their loaded
/// bodies — is deleted, and another conversation's rows are left exactly as they were.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. The rows carry no owner, only a conversation id, so every table is counted directly for both
/// the erased and the surviving conversation. Skipped when no Postgres is available, like the rest of this collection.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresObservabilityStoreErasureTests
{
    private readonly PostgresFixture _fixture;

    public PostgresObservabilityStoreErasureTests(PostgresFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task EraseSubjectAsync_SubjectsConversations_RemovesTheirRowsInEveryTableOnly()
    {
        _fixture.SkipIfUnavailable();
        var mine = _fixture.NewConversationId();
        var theirs = _fixture.NewConversationId();
        try
        {
            await SeedConversationAsync(mine);
            await SeedConversationAsync(theirs);
            (await CountsAsync(mine)).Should().Be(new TableCounts(1, 2, 1, 2, 2), "the seed must exist before erasing");

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            var removed = await store.EraseSubjectAsync(
                new ErasureSubject("user-a", [mine, _fixture.NewConversationId()]), CancellationToken.None);

            removed.Should().Be(1 + 2 + 2 + 2, "sessions, audit rows, snapshots and loaded bodies; cascaded messages are not counted");
            (await CountsAsync(mine)).Should().Be(new TableCounts(0, 0, 0, 0, 0), "nothing of the person may remain");
            (await CountsAsync(theirs)).Should().Be(new TableCounts(1, 2, 1, 2, 2), "another conversation must be untouched");
        }
        finally
        {
            await CleanUpLoadedBodiesAsync(mine, theirs);
        }
    }

    [SkippableFact]
    public async Task EraseSubjectAsync_PlannedTaskModelSteps_AreErasedLikeConversations()
    {
        // #906. A model step's session is recorded under its own conversation id, which only the plan store's record of the
        // run leads to.
        _fixture.SkipIfUnavailable();
        var step = _fixture.NewConversationId();
        var theirs = _fixture.NewConversationId();
        try
        {
            await SeedConversationAsync(step);
            await SeedConversationAsync(theirs);

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            await store.EraseSubjectAsync(
                new ErasureSubject("user-a", [], new PlanRunTraces([step], ["job-1"], ["planrun:plan-1"])),
                CancellationToken.None);

            (await CountsAsync(step)).Should().Be(new TableCounts(0, 0, 0, 0, 0));
            (await CountsAsync(theirs)).Should().Be(new TableCounts(1, 2, 1, 2, 2));
        }
        finally
        {
            await CleanUpLoadedBodiesAsync(step, theirs);
        }
    }

    [SkippableFact]
    public async Task EraseSubjectAsync_NoConversations_ReturnsZeroAndChangesNothing()
    {
        _fixture.SkipIfUnavailable();
        var theirs = _fixture.NewConversationId();
        try
        {
            await SeedConversationAsync(theirs);

            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
            (await store.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None)).Should().Be(0);
            (await store.EraseSubjectAsync(
                new ErasureSubject("user-a", [_fixture.NewConversationId()]), CancellationToken.None)).Should().Be(0);

            (await CountsAsync(theirs)).Should().Be(new TableCounts(1, 2, 1, 2, 2));
        }
        finally
        {
            await CleanUpLoadedBodiesAsync(theirs);
        }
    }

    /// <summary>One session with a message and two audit rows, two context snapshots, and two loaded bodies.</summary>
    private async Task SeedConversationAsync(string conversationId)
    {
        var sessionId = Guid.NewGuid();
        await _fixture.ExecuteAsync(
            "INSERT INTO sessions (id, conversation_id, agent_name) VALUES ($1, $2, 'erasure-agent')",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = conversationId });
        await _fixture.ExecuteAsync(
            "INSERT INTO session_messages (session_id, turn_index, role, content_full) VALUES ($1, 0, 'user', $2)",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = conversationId });

        for (var turn = 0; turn < 2; turn++)
        {
            await _fixture.ExecuteAsync(
                "INSERT INTO audit_log (operation, source, session_id, metadata) "
                + "VALUES ('erasure-test', 'harness', $1, "
                + "jsonb_build_object('run_tag', $2::text, 'conversation_id', $3::text))",
                new NpgsqlParameter { Value = sessionId },
                new NpgsqlParameter { Value = _fixture.RunTag },
                new NpgsqlParameter { Value = conversationId });
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshots (conversation_id, turn_index, turn_id) VALUES ($1, $2, $3)",
                new NpgsqlParameter { Value = conversationId },
                new NpgsqlParameter { Value = turn },
                new NpgsqlParameter { Value = $"t-{turn}" });
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshot_loaded_bodies (conversation_id, turn_index, loaded_index, body) "
                + "VALUES ($1, $2, 0, 'loaded body')",
                new NpgsqlParameter { Value = conversationId }, new NpgsqlParameter { Value = turn });
        }
    }

    private async Task<TableCounts> CountsAsync(string conversationId)
    {
        var id = new NpgsqlParameter { Value = conversationId };
        // Counted by the conversation id each seed wrote into the row itself, never through the session: deleting a
        // session only nulls an audit row's link, so a join through sessions would read 0 for a row left behind.
        return new TableCounts(
            await CountAsync("SELECT count(*) FROM sessions WHERE conversation_id = $1", id),
            await CountAsync("SELECT count(*) FROM audit_log WHERE metadata->>'conversation_id' = $1", id),
            await CountAsync("SELECT count(*) FROM session_messages WHERE content_full = $1", id),
            await CountAsync("SELECT count(*) FROM context_snapshots WHERE conversation_id = $1", id),
            await CountAsync("SELECT count(*) FROM context_snapshot_loaded_bodies WHERE conversation_id = $1", id));
    }

    private async Task<int> CountAsync(string sql, NpgsqlParameter parameter) =>
        await _fixture.QueryScalarAsync<int>(sql, parameter);

    /// <summary>
    /// The fixture's own cleanup does not reach this table; survivors are removed here. An erased conversation's
    /// audit rows can only be found through its session, so a surviving audit row is also cleaned by run tag in the
    /// fixture's disposal.
    /// </summary>
    private async Task CleanUpLoadedBodiesAsync(params string[] conversationIds)
    {
        await _fixture.ExecuteAsync(
            "DELETE FROM context_snapshot_loaded_bodies WHERE conversation_id = ANY($1)",
            new NpgsqlParameter { Value = conversationIds });
    }

    private sealed record TableCounts(int Sessions, int AuditRows, int Messages, int Snapshots, int LoadedBodies);
}
