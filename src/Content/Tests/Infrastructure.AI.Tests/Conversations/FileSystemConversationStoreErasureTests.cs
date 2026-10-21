using Application.AI.Common.Models.Conversations;
using Domain.Common.Compliance;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Right-to-erasure (#900) for <see cref="FileSystemConversationStore"/>: every conversation file the person owns is
/// removed, and every file anyone else owns is left exactly as it was.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. The eraser removes exactly the subject's listed conversations: one created after that list
/// was read is kept for a second request, because what it produced was never cleared.
/// </remarks>
public sealed class FileSystemConversationStoreErasureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemConversationStore _store;

    /// <summary>Creates an isolated conversations directory and the store that writes into it.</summary>
    public FileSystemConversationStoreErasureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convstore-erase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = _tempDir }),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<FileSystemConversationStore>.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EraseSubjectAsync_TwoOwners_RemovesOnlyTheSubjectsConversations()
    {
        var first = await SeedAsync("user-a", "first");
        var second = await SeedAsync("user-a", "second");
        var theirs = await SeedAsync("user-b", "not yours");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", [first, second]), CancellationToken.None);

        removed.Should().Be(2);
        (await _store.ListAsync("user-a")).Should().BeEmpty();
        var survivor = (await _store.ListAsync("user-b")).Should().ContainSingle().Subject;
        survivor.Id.Should().Be(theirs);
        survivor.Messages.Should().ContainSingle().Which.Content.Should().Be("not yours");
    }

    [Fact]
    public async Task EraseSubjectAsync_ConversationNotListedInTheSubject_IsKept()
    {
        // The subject lists the conversations whose dependent records were already cleared; one started after that list
        // was read still has its records, so it is left for a second request.
        var listed = await SeedAsync("user-a", "listed");
        var startedMeanwhile = await SeedAsync("user-a", "started meanwhile");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("user-a", [listed]), CancellationToken.None);

        removed.Should().Be(1);
        (await _store.ListAsync("user-a")).Should().ContainSingle().Which.Id.Should().Be(startedMeanwhile);
    }

    [Fact]
    public async Task EraseSubjectAsync_AnotherOwnersConversationIdInTheSubject_IsRefused()
    {
        var theirs = await SeedAsync("user-b", "not yours");

        var act = () => _store.EraseSubjectAsync(new ErasureSubject("user-a", [theirs]), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("the store's ownership check applies to erasure exactly as to a delete");
        (await _store.ListAsync("user-b")).Should().ContainSingle().Which.Id.Should().Be(theirs);
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerDiffersOnlyInCase_RemovesOnlyTheExactOwner()
    {
        var upper = await SeedAsync("Alice", "upper");
        var lower = await SeedAsync("alice", "lower");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("Alice", [upper]), CancellationToken.None);

        removed.Should().Be(1);
        (await _store.ListAsync("Alice")).Should().BeEmpty();
        (await _store.ListAsync("alice")).Should().ContainSingle()
            .Which.Id.Should().Be(lower, "a differently-cased owner is a different person");
    }

    [Fact]
    public async Task EraseSubjectAsync_OwnerWithNothingStored_ReturnsZeroAndChangesNothing()
    {
        var theirs = await SeedAsync("user-b", "kept");

        var removed = await _store.EraseSubjectAsync(new ErasureSubject("nobody", []), CancellationToken.None);

        removed.Should().Be(0);
        (await _store.ListAsync("user-b")).Should().ContainSingle().Which.Id.Should().Be(theirs);
    }

    private async Task<string> SeedAsync(string owner, string content)
    {
        var record = await _store.CreateAsync("agent", owner);
        await _store.AppendMessageAsync(
            record.Id, owner, new ConversationMessage(Guid.NewGuid(), MessageRole.User, content, DateTimeOffset.UtcNow));
        return record.Id;
    }
}
