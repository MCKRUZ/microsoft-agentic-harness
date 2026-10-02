using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Tests.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="FileSystemConversationStore"/> against the shared
/// <see cref="ConversationStoreContractTests"/>, plus the few behaviours that only make sense for a
/// store whose records are files.
/// </summary>
public sealed class FileSystemConversationStoreTests : ConversationStoreContractTests, IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemConversationStore _store;

    /// <summary>Creates an isolated conversations directory and the store that writes into it.</summary>
    public FileSystemConversationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convstore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _store = new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = _tempDir }),
            Clock,
            NullLogger<FileSystemConversationStore>.Instance);
    }

    /// <inheritdoc />
    protected override IConversationStore Store => _store;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // --- Locking granularity (#759) ---

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task AHeldConversation_DoesNotBlockReadsOrWritesOnAnotherConversation()
    {
        // One process-wide lock made every conversation wait for whichever one was doing I/O. Holding
        // conversation A's lock stands in for A being mid-read or mid-write.
        var a = await Store.CreateAsync("agent", Owner);
        var b = await Store.CreateAsync("agent", Owner);

        using var held = await _store.HoldConversationAsync(a.Id);

        var read = await Store.GetAsync(b.Id, Owner).WaitAsync(TimeSpan.FromSeconds(10));
        await Store.AppendMessageAsync(b.Id, Owner, UserMessage("not blocked"))
            .WaitAsync(TimeSpan.FromSeconds(10));

        read.Should().NotBeNull();
    }

    [Fact]
    public async Task AHeldConversation_QueuesOperationsOnItselfUntilReleased()
    {
        var a = await Store.CreateAsync("agent", Owner);
        var held = await _store.HoldConversationAsync(a.Id);

        var append = Store.AppendMessageAsync(a.Id, Owner, UserMessage("waits"));
        await Task.Delay(Settle);
        append.IsCompleted.Should().BeFalse("the conversation's own lock is held");

        held.Dispose();
        await append.WaitAsync(TimeSpan.FromSeconds(10));

        (await Store.GetAsync(a.Id, Owner))!.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task ListAsync_WaitsForAHeldConversationRatherThanReadingAroundIt()
    {
        // The listing's migration path rewrites a file it has just read; doing that outside the file's
        // lock could overwrite a message appended in between.
        var a = await Store.CreateAsync("agent", Owner);
        var held = await _store.HoldConversationAsync(a.Id);

        var list = Store.ListAsync(Owner);
        await Task.Delay(Settle);
        list.IsCompleted.Should().BeFalse("it must take each conversation's lock before reading its file");

        held.Dispose();

        (await list.WaitAsync(TimeSpan.FromSeconds(10))).Select(r => r.Id).Should().Contain(a.Id);
    }

    [Fact]
    public async Task ListAsync_ConversationDeletedAfterTheDirectoryWasListed_IsSkippedQuietly()
    {
        // The directory is enumerated before any lock is taken, so a delete can land between that and
        // the list reaching the file. The old process-wide lock made this impossible; without the
        // existence check it surfaces as a "failed to deserialize" warning for a conversation that
        // was simply deleted.
        var logger = new CapturingLogger<FileSystemConversationStore>();
        var store = new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = _tempDir }), Clock, logger);
        var a = await store.CreateAsync("agent", Owner);
        var b = await store.CreateAsync("agent", Owner);
        var held = await store.HoldConversationAsync(a.Id);

        var list = store.ListAsync(Owner);
        await Task.Delay(Settle);
        File.Delete(Path.Combine(_tempDir, $"{a.Id}.json"));
        held.Dispose();

        var listed = await list.WaitAsync(TimeSpan.FromSeconds(10));

        listed.Select(r => r.Id).Should().Contain(b.Id).And.NotContain(a.Id);
        logger.Logged(LogLevel.Warning, "Failed to deserialize").Should().BeFalse(
            "a deleted conversation is not a corrupt one");
    }


    [Fact]
    public async Task ConcurrentAppendsToOneConversation_AreNeverLost()
    {
        // The lock exists to make read-modify-write atomic. Without it, concurrent appends read the
        // same transcript and the last writer wins.
        var a = await Store.CreateAsync("agent", Owner);

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            Store.AppendMessageAsync(a.Id, Owner, UserMessage($"m{i}"))));

        (await Store.GetAsync(a.Id, Owner))!.Messages.Should().HaveCount(24);
    }

    [Fact]
    public async Task ConcurrentAppendsAcrossConversations_AreEachKeptWhole()
    {
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
            ids.Add((await Store.CreateAsync("agent", Owner)).Id);

        await Task.WhenAll(ids.SelectMany(id => Enumerable.Range(0, 8).Select(i =>
            Store.AppendMessageAsync(id, Owner, UserMessage($"{id}-{i}")))));

        foreach (var id in ids)
            (await Store.GetAsync(id, Owner))!.Messages.Should().HaveCount(8);
    }

    // --- Directory permissions (#640, following #527's precedent) ---

    [Fact]
    public void Constructor_CreatesConversationsDirectoryOwnerOnly()
    {
        // #640: this store holds full conversation transcripts -- must never inherit whatever the
        // process umask/ACL happens to grant. Uses its own fresh directory (unlike this fixture's own
        // _tempDir, pre-created above for every other test here) so this test exercises the
        // freshly-created-directory path specifically, rather than #670's separate already-exists
        // retroactive-reassert path (covered by OwnerOnlyDirectoryHelperTests instead).
        var freshDir = Path.Combine(Path.GetTempPath(), $"convstore-permcheck-{Guid.NewGuid():N}");
        try
        {
            _ = new FileSystemConversationStore(
                Options.Create(new ConversationsConfig { ConversationsPath = freshDir }),
                Clock,
                NullLogger<FileSystemConversationStore>.Instance);

            freshDir.ShouldBeOwnerOnlyDirectory();
        }
        finally
        {
            if (Directory.Exists(freshDir))
                Directory.Delete(freshDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_WritesJsonFileAtExpectedPath()
    {
        var record = await _store.CreateAsync("agent", Owner);

        File.Exists(Path.Combine(_tempDir, $"{record.Id}.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessageAsync_LeavesNoStagingFileBehind()
    {
        // Writes stage through a .tmp path and are moved into place. A .tmp left behind means the
        // move did not happen, and the record on disk is the pre-append one.
        var record = await _store.CreateAsync("agent", Owner);

        await _store.AppendMessageAsync(record.Id, Owner, UserMessage("hello"));

        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
        File.Exists(Path.Combine(_tempDir, $"{record.Id}.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile()
    {
        var record = await _store.CreateAsync("agent", Owner);
        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");

        await _store.DeleteAsync(record.Id, Owner);

        File.Exists(filePath).Should().BeFalse();
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task DeleteAsync_RecordTooCorruptToNameAnOwner_StillDeletesIt(string corrupt)
    {
        // Ownership is read from the file here, so a file that cannot be read has no owner to check.
        // It must still be deletable: refusing would leave it stuck in the directory with no way to
        // remove it through the API, and would protect nothing — writing that file needed directory
        // access, and anyone with that can delete it without asking this store.
        //
        // The cases matter. Deserialize returns null only for the literal `null`; truncated and empty
        // content throw instead, which is the shape corruption actually takes.
        var record = await _store.CreateAsync("agent", Owner);
        var path = Path.Combine(_tempDir, $"{record.Id}.json");
        await File.WriteAllTextAsync(path, corrupt);

        var deleted = await _store.DeleteAsync(record.Id, Owner);

        deleted.Should().BeTrue();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ConversationIdEscapingTheBasePath_ThrowsArgumentException()
    {
        // The conversation id becomes a file name, so an id is an untrusted path segment.
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync("../evil", Owner));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync("../../etc/passwd", Owner));
    }

    [Fact]
    public async Task GetAsync_RecordWrittenBeforeMessageIdsExisted_BackfillsThemOnRead()
    {
        // Records written by an earlier version have no message ids at all, which deserialize as
        // Guid.Empty. They are backfilled and rewritten on read, so retry/edit still has something
        // to reference. The SQLite store has no such history and normalises on write instead, which
        // is why this lives here rather than in the contract.
        var record = await _store.CreateAsync("agent", Owner);
        var path = Path.Combine(_tempDir, $"{record.Id}.json");
        await File.WriteAllTextAsync(path, LegacyRecordJson(record.Id));

        var loaded = await _store.GetAsync(record.Id, Owner);

        loaded!.Messages.Should().ContainSingle().Which.Id.Should().NotBe(Guid.Empty);
        (await _store.GetAsync(record.Id, Owner))!.Messages[0].Id
            .Should().Be(loaded.Messages[0].Id, "the backfilled id must have been persisted");
    }

    private static string LegacyRecordJson(string conversationId) =>
        $$"""
        {
          "id": "{{conversationId}}",
          "agentName": "agent",
          "userId": "user1",
          "createdAt": "2026-01-01T00:00:00+00:00",
          "updatedAt": "2026-01-01T00:00:00+00:00",
          "messages": [
            { "role": "User", "content": "legacy", "timestamp": "2026-01-01T00:00:00+00:00" }
          ]
        }
        """;
}
