using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task CreateAsync_WritesJsonFileAtExpectedPath()
    {
        var record = await _store.CreateAsync("agent", "user1");

        File.Exists(Path.Combine(_tempDir, $"{record.Id}.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessageAsync_LeavesNoStagingFileBehind()
    {
        // Writes stage through a .tmp path and are moved into place. A .tmp left behind means the
        // move did not happen, and the record on disk is the pre-append one.
        var record = await _store.CreateAsync("agent", "user1");

        await _store.AppendMessageAsync(record.Id, UserMessage("hello"));

        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
        File.Exists(Path.Combine(_tempDir, $"{record.Id}.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");

        await _store.DeleteAsync(record.Id);

        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task ConversationIdEscapingTheBasePath_ThrowsArgumentException()
    {
        // The conversation id becomes a file name, so an id is an untrusted path segment.
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync("../evil"));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task GetAsync_RecordWrittenBeforeMessageIdsExisted_BackfillsThemOnRead()
    {
        // Records written by an earlier version have no message ids at all, which deserialize as
        // Guid.Empty. They are backfilled and rewritten on read, so retry/edit still has something
        // to reference. The SQLite store has no such history and normalises on write instead, which
        // is why this lives here rather than in the contract.
        var record = await _store.CreateAsync("agent", "user1");
        var path = Path.Combine(_tempDir, $"{record.Id}.json");
        await File.WriteAllTextAsync(path, LegacyRecordJson(record.Id));

        var loaded = await _store.GetAsync(record.Id);

        loaded!.Messages.Should().ContainSingle().Which.Id.Should().NotBe(Guid.Empty);
        (await _store.GetAsync(record.Id))!.Messages[0].Id
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
