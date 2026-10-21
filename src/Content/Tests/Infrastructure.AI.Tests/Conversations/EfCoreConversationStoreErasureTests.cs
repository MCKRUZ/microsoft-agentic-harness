using Application.AI.Common.Models.Conversations;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Right-to-erasure (#900) for <see cref="EfCoreConversationStore"/>: the person's listed conversations, and every
/// message in them, are removed, and nothing anyone else owns is touched.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. Both directions are asserted against the tables, not through the store's interface, because
/// the message rows go through a database-level cascade the interface cannot show. Runs against a real SQLite file for
/// the same reason <see cref="EfCoreConversationStoreTests"/> does.
/// </remarks>
public sealed class EfCoreConversationStoreErasureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly EfCoreConversationStore _store;

    /// <summary>Creates an isolated on-disk database and the store that writes into it.</summary>
    public EfCoreConversationStoreErasureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convdb-erase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));
        _store = new EfCoreConversationStore(
            _contextFactory,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<EfCoreConversationStore>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EraseSubjectAsync_TwoOwners_RemovesOnlyTheSubjectsConversationsAndMessages()
    {
        var mine1 = await SeedAsync("user-a", messages: 2);
        var mine2 = await SeedAsync("user-a", messages: 1);
        var theirs = await SeedAsync("user-b", messages: 3);
        (await CountConversationsAsync("user-a")).Should().Be(2, "the seed must exist before erasing");
        (await CountMessagesAsync(mine1, mine2)).Should().Be(3);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", [mine1, mine2]), CancellationToken.None);

        removed.Should().Be(2);
        (await CountConversationsAsync("user-a")).Should().Be(0);
        (await CountMessagesAsync(mine1, mine2)).Should().Be(0, "messages must go with their conversation");
        (await CountConversationsAsync("user-b")).Should().Be(1, "another person's conversation must survive");
        (await CountMessagesAsync(theirs)).Should().Be(3, "another person's messages must survive");
    }

    [Fact]
    public async Task EraseSubjectAsync_ConversationNotListedInTheSubject_IsKept()
    {
        // The subject lists the conversations whose dependent records were already cleared. One started after that
        // list was read still has its records, so deleting it would leave them with nothing that leads to them.
        var listed = await SeedAsync("user-a", messages: 1);
        var startedMeanwhile = await SeedAsync("user-a", messages: 1);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", [listed]), CancellationToken.None);

        removed.Should().Be(1);
        (await CountMessagesAsync(startedMeanwhile)).Should().Be(1);
        (await CountConversationsAsync("user-a")).Should().Be(1);
    }

    [Fact]
    public async Task EraseSubjectAsync_AnotherOwnersConversationIdInTheSubject_IsNotErased()
    {
        var theirs = await SeedAsync("user-b", messages: 2);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", [theirs]), CancellationToken.None);

        removed.Should().Be(0, "a conversation is erased only while it still belongs to the person");
        (await CountMessagesAsync(theirs)).Should().Be(2);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerDiffersOnlyInCase_RemovesOnlyTheExactOwner()
    {
        // Conversations store the user id as issued. "Alice" and "alice" are two different identities to this store,
        // so erasing one must never reach the other's conversations.
        var upper = await SeedAsync("Alice", messages: 1);
        var lower = await SeedAsync("alice", messages: 1);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("Alice", [upper, lower]), CancellationToken.None);

        removed.Should().Be(1);
        (await CountConversationsAsync("Alice")).Should().Be(0);
        (await CountMessagesAsync(upper)).Should().Be(0);
        (await CountConversationsAsync("alice")).Should().Be(1, "a differently-cased owner is a different person");
        (await CountMessagesAsync(lower)).Should().Be(1);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingStored_ReturnsZeroAndChangesNothing()
    {
        var theirs = await SeedAsync("user-b", messages: 2);

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await CountConversationsAsync("user-b")).Should().Be(1);
        (await CountMessagesAsync(theirs)).Should().Be(2);
    }

    [Fact]
    public async Task EraseSubjectAsync_WithAnotherConnectionOpen_EmptiesTheWriteAheadLog()
    {
        // A running host keeps connections open, so SQLite's write-ahead log is never folded in on close and keeps the
        // frames that wrote the transcript; the store must empty it after erasing (#900). The open connection here stands
        // in for the host's pool, and keeps the log from being folded in by the last connection closing.
        var databasePath = Path.Combine(_tempDir, "conversations.db");
        var erased = await SeedAsync("user-a", messages: 2);
        await using var held = new Microsoft.Data.Sqlite.SqliteConnection($"DataSource={databasePath};Pooling=False");
        await held.OpenAsync();
        await using (var walMode = held.CreateCommand())
        {
            // The mode the host's databases run in (the composition test sees their -wal files); persistent per file.
            walMode.CommandText = "PRAGMA journal_mode=WAL;";
            await walMode.ExecuteNonQueryAsync();
        }

        await SeedAsync("user-b", messages: 1);
        new FileInfo(databasePath + "-wal").Length.Should().BeGreaterThan(0, "the seed must leave frames in the log");

        await _store.EraseSubjectAsync(new ErasureSubject("user-a", [erased]), CancellationToken.None);

        new FileInfo(databasePath + "-wal").Length.Should().Be(0, "the erased transcript's frames must not stay in the log");
    }

    private async Task<string> SeedAsync(string owner, int messages)
    {
        var record = await _store.CreateAsync("agent", owner);
        for (var i = 0; i < messages; i++)
        {
            await _store.AppendMessageAsync(
                record.Id, owner, new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"m{i}", DateTimeOffset.UtcNow));
        }

        return record.Id;
    }

    private async Task<int> CountConversationsAsync(string owner)
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.Conversations.CountAsync(c => c.UserId == owner);
    }

    private async Task<int> CountMessagesAsync(params string[] conversationIds)
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.ConversationMessages.CountAsync(m => conversationIds.Contains(m.ConversationId));
    }
}
