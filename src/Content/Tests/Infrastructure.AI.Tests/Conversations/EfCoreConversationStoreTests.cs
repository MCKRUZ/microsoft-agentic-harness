using Application.AI.Common.Interfaces.AI;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="EfCoreConversationStore"/> against the shared
/// <see cref="ConversationStoreContractTests"/>, plus the guarantees only this implementation makes.
/// </summary>
/// <remarks>
/// Runs against a real SQLite <em>file</em>, not <c>:memory:</c>. An in-memory database lives inside
/// one connection, which would quietly serialise every writer and make the concurrency test below
/// prove nothing — the very property it exists to check.
/// </remarks>
public sealed class EfCoreConversationStoreTests : ConversationStoreContractTests, IDisposable
{
    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly EfCoreConversationStore _store;

    /// <summary>Creates an isolated on-disk database and the store that writes into it.</summary>
    public EfCoreConversationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));
        _store = BuildStore();
    }

    /// <inheritdoc />
    protected override IConversationStore Store => _store;

    /// <inheritdoc />
    public void Dispose()
    {
        // Deletes the WAL and shared-memory sidecars along with the database itself.
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ConcurrentAppends_FromIndependentStoreInstances_AllSurvive()
    {
        // This is the whole reason the store was replaced. Two instances stand in for two host
        // processes: they share nothing in memory, so any write serialisation has to come from the
        // database. The file-backed store fails this — its lock is one SemaphoreSlim per instance,
        // so two instances read the same record, each append their own message, and each rewrite
        // the file with the other's message missing.
        const int perStore = 15;
        var second = BuildStore();
        var record = await _store.CreateAsync("agent", "user1");

        var fromFirst = Enumerable.Range(0, perStore)
            .Select(i => _store.AppendMessageAsync(record.Id, UserMessage($"a-{i}")));
        var fromSecond = Enumerable.Range(0, perStore)
            .Select(i => second.AppendMessageAsync(record.Id, UserMessage($"b-{i}")));

        await Task.WhenAll(fromFirst.Concat(fromSecond));

        var messages = (await _store.GetAsync(record.Id))!.Messages;
        messages.Should().HaveCount(perStore * 2);
        messages.Select(m => m.Content).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SeparateStoreInstance_ReadsWhatAnotherWrote()
    {
        // Durability across instances, which is what lets a second host continue a conversation the
        // first one started. Nothing is cached in the writer for the reader to be served from.
        var record = await _store.CreateAsync("agent", "user1");
        await _store.AppendMessageAsync(record.Id, UserMessage("written by the first store"));

        var reader = BuildStore();

        var loaded = await reader.GetAsync(record.Id);
        loaded!.Messages.Should().ContainSingle().Which.Content.Should().Be("written by the first store");
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheConversationsMessageRows()
    {
        // Through the interface a deleted conversation is simply gone; only a look at the tables
        // shows whether its messages went with it or were left orphaned.
        var record = await _store.CreateAsync("agent", "user1");
        await _store.AppendMessageAsync(record.Id, UserMessage("hello"));

        await _store.DeleteAsync(record.Id);

        await using var context = _contextFactory.CreateDbContext();
        (await context.ConversationMessages.CountAsync(m => m.ConversationId == record.Id))
            .Should().Be(0, "the foreign key cascade must take the messages with the conversation");
    }

    [Fact]
    public async Task AppendMessage_InsertsOneRowAndRewritesNothing()
    {
        // The property that makes concurrent appends safe: an append adds a row and leaves the
        // existing rows — including their ordinals — untouched.
        var record = await _store.CreateAsync("agent", "user1");
        await _store.AppendMessageAsync(record.Id, UserMessage("first"));

        await using (var before = _contextFactory.CreateDbContext())
        {
            var firstOrdinal = await before.ConversationMessages
                .Where(m => m.ConversationId == record.Id)
                .Select(m => m.Ordinal)
                .SingleAsync();

            await _store.AppendMessageAsync(record.Id, UserMessage("second"));

            await using var after = _contextFactory.CreateDbContext();
            var ordinals = await after.ConversationMessages
                .Where(m => m.ConversationId == record.Id)
                .OrderBy(m => m.Ordinal)
                .Select(m => m.Ordinal)
                .ToListAsync();

            ordinals.Should().HaveCount(2);
            ordinals[0].Should().Be(firstOrdinal, "the existing message must not be rewritten");
            ordinals[1].Should().BeGreaterThan(firstOrdinal, "a later message must sort after an earlier one");
        }
    }

    [Fact]
    public void Database_IsInWalMode()
    {
        // EF Core's SQLite creator sets this itself — an explicit PRAGMA was written, measured to be
        // redundant, and removed. Asserted anyway because a provider change could withdraw it
        // without any other test noticing; RegisterConversationDbContext explains what rests on it.
        using var context = _contextFactory.CreateDbContext();

        var connection = context.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Convert.ToString(command.ExecuteScalar())
            .Should().BeEquivalentTo("wal");
    }

    private EfCoreConversationStore BuildStore() =>
        new(
            _contextFactory,
            Clock,
            NullLogger<EfCoreConversationStore>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));

    /// <summary>
    /// Hands out contexts over one on-disk database, with pooling disabled so the file is not still
    /// held by a pooled connection when the fixture deletes it.
    /// </summary>
    private sealed class TestConversationDbContextFactory(string databasePath)
        : IDbContextFactory<ConversationDbContext>
    {
        private readonly DbContextOptions<ConversationDbContext> _options =
            new DbContextOptionsBuilder<ConversationDbContext>()
                .UseSqlite($"DataSource={databasePath};Pooling=False")
                .Options;

        public ConversationDbContext CreateDbContext() => new(_options);
    }
}
