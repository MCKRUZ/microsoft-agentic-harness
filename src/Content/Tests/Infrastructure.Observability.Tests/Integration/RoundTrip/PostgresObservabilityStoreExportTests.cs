using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.RoundTrip;

/// <summary>
/// Personal-data export (#902) for <see cref="PostgresObservabilityStore"/>: the copy holds every row recorded for the
/// person's conversations, in every table erasure deletes from, and none of another conversation's.
/// </summary>
/// <remarks>
/// The rows carry no owner, only a conversation id, so a row is the person's only through that id. Three tables are reached
/// by erasure only through the cascade from <c>sessions</c>; the copy reads them through the session, and each is checked
/// here by name so one cannot drop out silently. Skipped when no Postgres is available, like the rest of this collection.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresObservabilityStoreExportTests
{
    private readonly PostgresFixture _fixture;

    public PostgresObservabilityStoreExportTests(PostgresFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ExportSubjectAsync_SubjectsConversations_ReturnsTheirRowsFromEveryTableOnly()
    {
        _fixture.SkipIfUnavailable();
        var mine = _fixture.NewConversationId();
        var step = _fixture.NewConversationId();
        var theirs = _fixture.NewConversationId();
        try
        {
            await SeedConversationAsync(mine);
            await SeedConversationAsync(step);
            await SeedConversationAsync(theirs);
            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

            var records = await store.ExportSubjectAsync(
                new ExportSubject("user-a", [mine], new PlanRunTraces([step], [], [])), CancellationToken.None);

            var perTable = records.GroupBy(r => r.GetProperty("table").GetString()!).ToDictionary(g => g.Key, g => g.Count());
            perTable.Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["sessions"] = 2,
                ["session_messages"] = 2,
                ["tool_executions"] = 2,
                ["safety_events"] = 2,
                ["audit_log"] = 4,
            }, "the person's conversation and their planned task's model step, in every table that records the person");
            var text = string.Join("\n", records.Select(r => r.GetRawText()));
            text.Should().Contain(mine).And.Contain(step).And.Contain($"{mine} said");
            text.Should().NotContain(theirs, "another conversation's rows are not the person's");
            text.Should().NotContain(OperatorText,
                "registration bodies, injected context, raw errors and audit metadata are the operator's, readable today only by observers");
        }
        finally
        {
            await CleanUpAsync(mine, step, theirs);
        }
    }

    [SkippableFact]
    public async Task ExportSubjectAsync_NoConversations_ReturnsNothing()
    {
        _fixture.SkipIfUnavailable();
        var theirs = _fixture.NewConversationId();
        try
        {
            await SeedConversationAsync(theirs);
            using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

            (await store.ExportSubjectAsync(new ExportSubject("user-a", []), CancellationToken.None)).Should().BeEmpty();
            (await store.ExportSubjectAsync(new ExportSubject("user-a", [_fixture.NewConversationId()]), CancellationToken.None))
                .Should().BeEmpty();
        }
        finally
        {
            await CleanUpAsync(theirs);
        }
    }

    /// <summary>Planted in every operator-only column the seed fills; it must never reach a person's copy.</summary>
    private const string OperatorText = "OPERATOR-ONLY-REGISTRATION";

    /// <summary>
    /// One session with the person's message, an injected system-context message, a tool execution, a safety event and two
    /// audit rows, plus two context snapshots and two loaded bodies. Every operator-only column carries
    /// <see cref="OperatorText"/>.
    /// </summary>
    private async Task SeedConversationAsync(string conversationId)
    {
        var sessionId = Guid.NewGuid();
        await _fixture.ExecuteAsync(
            "INSERT INTO sessions (id, conversation_id, agent_name, error_message) VALUES ($1, $2, 'export-agent', $3)",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = conversationId },
            new NpgsqlParameter { Value = OperatorText });
        await _fixture.ExecuteAsync(
            "INSERT INTO session_messages (session_id, turn_index, role, source, content_full) "
            + "VALUES ($1, 0, 'user', 'user_message', $2)",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = $"{conversationId} said" });
        await _fixture.ExecuteAsync(
            "INSERT INTO session_messages (session_id, turn_index, role, source, content_full) "
            + "VALUES ($1, 0, 'system', 'system_context', $2)",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = OperatorText });
        await _fixture.ExecuteAsync(
            "INSERT INTO tool_executions (session_id, tool_name, tool_source, status) VALUES ($1, $2, 'keyed_di', 'success')",
            new NpgsqlParameter { Value = sessionId }, new NpgsqlParameter { Value = conversationId });
        await _fixture.ExecuteAsync(
            "INSERT INTO safety_events (session_id, phase, outcome) VALUES ($1, 'prompt', 'pass')",
            new NpgsqlParameter { Value = sessionId });

        for (var turn = 0; turn < 2; turn++)
        {
            await _fixture.ExecuteAsync(
                "INSERT INTO audit_log (operation, source, session_id, metadata) "
                + "VALUES ('export-test', 'harness', $1, "
                + "jsonb_build_object('run_tag', $2::text, 'approver', $3::text))",
                new NpgsqlParameter { Value = sessionId },
                new NpgsqlParameter { Value = _fixture.RunTag },
                new NpgsqlParameter { Value = OperatorText });
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshots (conversation_id, turn_index, turn_id) VALUES ($1, $2, $3)",
                new NpgsqlParameter { Value = conversationId },
                new NpgsqlParameter { Value = turn },
                new NpgsqlParameter { Value = $"t-{turn}" });
            await _fixture.ExecuteAsync(
                "INSERT INTO context_snapshot_loaded_bodies (conversation_id, turn_index, loaded_index, body) "
                + "VALUES ($1, $2, 0, $3)",
                new NpgsqlParameter { Value = conversationId }, new NpgsqlParameter { Value = turn },
                new NpgsqlParameter { Value = OperatorText });
        }
    }

    /// <summary>Removes what the seeds wrote; the fixture's own cleanup does not reach the loaded-bodies table.</summary>
    private async Task CleanUpAsync(params string[] conversationIds)
    {
        await _fixture.ExecuteAsync(
            "DELETE FROM context_snapshot_loaded_bodies WHERE conversation_id = ANY($1)",
            new NpgsqlParameter { Value = conversationIds });
        await _fixture.ExecuteAsync(
            "DELETE FROM context_snapshots WHERE conversation_id = ANY($1)",
            new NpgsqlParameter { Value = conversationIds });
        await _fixture.ExecuteAsync(
            "DELETE FROM audit_log WHERE session_id IN (SELECT id FROM sessions WHERE conversation_id = ANY($1))",
            new NpgsqlParameter { Value = conversationIds });
        await _fixture.ExecuteAsync(
            "DELETE FROM sessions WHERE conversation_id = ANY($1)",
            new NpgsqlParameter { Value = conversationIds });
    }
}
