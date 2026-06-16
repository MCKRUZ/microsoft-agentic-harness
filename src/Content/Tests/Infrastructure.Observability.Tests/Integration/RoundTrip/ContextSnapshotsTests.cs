using Domain.AI.Context;
using FluentAssertions;
using Infrastructure.Observability.Persistence;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.RoundTrip;

/// <summary>
/// Round-trip tests for the Foresight context-snapshot Postgres impl. Skipped
/// when no local Postgres is available; otherwise asserts insert / latest /
/// list / batched-breakdowns behavior including idempotent replay.
/// </summary>
[Collection("Postgres")]
public sealed class ContextSnapshotsTests
{
    private readonly PostgresFixture _fixture;

    public ContextSnapshotsTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Apply the snapshot DDL idempotently in case the test database was
    /// initialized from an older schema bundle that pre-dated 02-context-snapshots.sql.
    /// </summary>
    private async Task EnsureSchemaAsync()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS context_snapshots (
                id              BIGSERIAL PRIMARY KEY,
                conversation_id TEXT        NOT NULL,
                turn_index      INTEGER     NOT NULL,
                turn_id         TEXT        NOT NULL,
                cat_system      INTEGER     NOT NULL DEFAULT 0,
                cat_agents      INTEGER     NOT NULL DEFAULT 0,
                cat_skills      INTEGER     NOT NULL DEFAULT 0,
                cat_tools       INTEGER     NOT NULL DEFAULT 0,
                cat_mcp         INTEGER     NOT NULL DEFAULT 0,
                cat_messages    INTEGER     NOT NULL DEFAULT 0,
                loaded_json     JSONB       NOT NULL DEFAULT '[]'::jsonb,
                captured_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT uq_context_snapshots_conv_turn
                    UNIQUE (conversation_id, turn_index)
            );
            """;

        await _fixture.ExecuteAsync(ddl);
    }

    private static ContextSnapshot MakeSnapshot(string convId, int turnIndex, int messages, int system)
        => new(
            ConversationId: convId,
            TurnIndex: turnIndex,
            TurnId: $"t-{turnIndex:D2}",
            CtxAfter: new CategoryBreakdown(System: system, Agents: 0, Skills: 0, Tools: 0, Mcp: 0, Messages: messages),
            Loaded:
            [
                new LoadedItem("User message", 50, ContextCategory.Messages, null),
                new LoadedItem("Assistant message", 80, ContextCategory.Messages, $"t-{turnIndex:D2}-assistant"),
            ],
            CapturedAtUtc: new DateTimeOffset(2026, 6, 1, 12, 0, turnIndex, TimeSpan.Zero));

    [SkippableFact]
    public async Task RoundTrip_RecordThenGetLatest_ReturnsExactSnapshot()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();
        var snapshot = MakeSnapshot(convId, turnIndex: 4, messages: 1200, system: 8200);

        await store.RecordContextSnapshotAsync(snapshot);
        var read = await store.GetLatestSnapshotAsync(convId);

        read.Should().NotBeNull();
        read!.ConversationId.Should().Be(convId);
        read.TurnIndex.Should().Be(4);
        read.TurnId.Should().Be("t-04");
        read.CtxAfter.Messages.Should().Be(1200);
        read.CtxAfter.System.Should().Be(8200);
        read.CtxAfter.Total.Should().Be(9400);
        read.Loaded.Should().HaveCount(2);
        read.Loaded[1].Reference.Should().Be("t-04-assistant");
    }

    [SkippableFact]
    public async Task RoundTrip_TwoTurns_GetLatestReturnsHighestTurnIndex()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 0, messages: 100, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 1, messages: 800, system: 5000));

        var latest = await store.GetLatestSnapshotAsync(convId);

        latest!.TurnIndex.Should().Be(1);
        latest.CtxAfter.Messages.Should().Be(800);
    }

    [SkippableFact]
    public async Task RoundTrip_ReplayWithSameTurnIndex_OverwritesIdempotently()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 0, messages: 100, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 0, messages: 200, system: 6000));

        var snapshots = await store.GetSnapshotsAsync(convId);

        snapshots.Should().HaveCount(1);
        snapshots[0].CtxAfter.Messages.Should().Be(200);
        snapshots[0].CtxAfter.System.Should().Be(6000);
    }

    [SkippableFact]
    public async Task GetSnapshots_OrdersByTurnIndexAscending()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convId = _fixture.NewConversationId();

        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 2, messages: 300, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 0, messages: 100, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convId, 1, messages: 200, system: 5000));

        var snapshots = await store.GetSnapshotsAsync(convId);

        snapshots.Select(s => s.TurnIndex).Should().Equal(0, 1, 2);
    }

    [SkippableFact]
    public async Task GetLatestBreakdowns_BatchedAcrossConversations_ReturnsLatestPerEach()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var convA = _fixture.NewConversationId();
        var convB = _fixture.NewConversationId();
        var convMissing = _fixture.NewConversationId();

        await store.RecordContextSnapshotAsync(MakeSnapshot(convA, 0, messages: 100, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convA, 1, messages: 700, system: 5000));
        await store.RecordContextSnapshotAsync(MakeSnapshot(convB, 0, messages: 250, system: 3000));

        var result = await store.GetLatestBreakdownsAsync([convA, convB, convMissing]);

        result.Should().HaveCount(2);
        result[convA].Messages.Should().Be(700);
        result[convA].System.Should().Be(5000);
        result[convB].Messages.Should().Be(250);
        result[convB].System.Should().Be(3000);
        result.Should().NotContainKey(convMissing);
    }

    [SkippableFact]
    public async Task GetLatestSnapshot_UnknownConversation_ReturnsNull()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var unknown = _fixture.NewConversationId();

        var result = await store.GetLatestSnapshotAsync(unknown);

        result.Should().BeNull();
    }

    [SkippableFact]
    public async Task GetLatestBreakdowns_EmptyInput_ReturnsEmpty()
    {
        _fixture.SkipIfUnavailable();
        await EnsureSchemaAsync();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);

        var result = await store.GetLatestBreakdownsAsync(Array.Empty<string>());

        result.Should().BeEmpty();
    }
}
