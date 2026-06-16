using Infrastructure.Observability.Persistence;
using Npgsql;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration.WriteSide;

/// <summary>
/// PR 6 deferreds: verifies the new content_full column on session_messages
/// persists correctly through RecordMessageAsync and surfaces back through
/// the new GetMessageByIdAsync read path. List-side reads still return
/// content_full = null so the dashboard list endpoint stays cheap.
/// </summary>
[Collection("Postgres")]
public sealed class MessageContentFullTests
{
    private readonly PostgresFixture _fixture;

    public MessageContentFullTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task RecordMessageAsync_WithContentFull_PersistsBothPreviewAndFull()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var fullBody = new string('a', 2000); // exceeds 500-char preview cap
        var messageId = await store.RecordMessageAsync(
            session.Id, turnIndex: 1, role: "user", source: "user_message",
            contentPreview: fullBody[..500], model: null,
            inputTokens: 0, outputTokens: 0, cacheRead: 0, cacheWrite: 0,
            costUsd: 0m, cacheHitPct: 0m, toolNames: null,
            contentFull: fullBody);

        var rows = await _fixture.QueryRowsAsync(
            "SELECT content_preview, content_full FROM session_messages WHERE id = $1",
            new NpgsqlParameter { Value = messageId });

        Assert.Single(rows);
        Assert.Equal(500, ((string)rows[0]["content_preview"]!).Length);
        Assert.Equal(2000, ((string)rows[0]["content_full"]!).Length);
        Assert.Equal(fullBody, rows[0]["content_full"]);
    }

    [SkippableFact]
    public async Task GetMessageByIdAsync_ExistingMessage_ReturnsContentFull()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        var messageId = await store.RecordMessageAsync(
            session.Id, turnIndex: 1, role: "user", source: "user_message",
            contentPreview: "preview", model: null,
            inputTokens: 0, outputTokens: 0, cacheRead: 0, cacheWrite: 0,
            costUsd: 0m, cacheHitPct: 0m, toolNames: null,
            contentFull: "the full body");

        var record = await store.GetMessageByIdAsync(session.Id, messageId);

        Assert.NotNull(record);
        Assert.Equal("preview", record!.ContentPreview);
        Assert.Equal("the full body", record.ContentFull);
    }

    [SkippableFact]
    public async Task GetMessageByIdAsync_DifferentSession_Returns404()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var ownerSession = await builder.CreateSessionAsync(status: "active");
        var otherSession = await builder.CreateSessionAsync(status: "active");

        var messageId = await store.RecordMessageAsync(
            ownerSession.Id, turnIndex: 1, role: "user", source: "user_message",
            contentPreview: "p", model: null,
            inputTokens: 0, outputTokens: 0, cacheRead: 0, cacheWrite: 0,
            costUsd: 0m, cacheHitPct: 0m, toolNames: null,
            contentFull: "f");

        // Cross-session traversal must 404 — protects the file-body deep-link
        // from leaking message bodies across session boundaries.
        var record = await store.GetMessageByIdAsync(otherSession.Id, messageId);

        Assert.Null(record);
    }

    [SkippableFact]
    public async Task GetSessionMessagesAsync_ListPath_ReturnsNullContentFull()
    {
        _fixture.SkipIfUnavailable();

        using var store = new PostgresObservabilityStore(_fixture.ConnectionString, _fixture.StoreLogger);
        var builder = new TestDataBuilder(_fixture);
        var session = await builder.CreateSessionAsync(status: "active");

        _ = await store.RecordMessageAsync(
            session.Id, turnIndex: 1, role: "user", source: "user_message",
            contentPreview: "preview", model: null,
            inputTokens: 0, outputTokens: 0, cacheRead: 0, cacheWrite: 0,
            costUsd: 0m, cacheHitPct: 0m, toolNames: null,
            contentFull: "the full body should NOT be in the list response");

        var list = await store.GetSessionMessagesAsync(session.Id);

        Assert.Single(list);
        Assert.Equal("preview", list[0].ContentPreview);
        // List endpoint deliberately returns null for content_full to keep
        // sessions-list payloads cheap. The detail endpoint is the only path
        // that pulls the full body.
        Assert.Null(list[0].ContentFull);
    }
}
