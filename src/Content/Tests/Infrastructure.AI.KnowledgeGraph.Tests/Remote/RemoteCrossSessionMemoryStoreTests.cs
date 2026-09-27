using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Remote;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// <see cref="RemoteCrossSessionMemoryStore"/> is a local, network-free stand-in — the remote
/// memory contract's <c>/cross-session/{action}</c> endpoints are deliberately inert for this
/// phase of the avatar-hosting migration. It behaves as an always-empty store.
/// </summary>
public sealed class RemoteCrossSessionMemoryStoreTests
{
    private static MemoryRecord Record() => new()
    {
        Id = "mem-1",
        Content = "some fact",
        Source = "test",
        Weight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        OwnerId = "user-1",
    };

    [Fact]
    public async Task RememberAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteCrossSessionMemoryStore();

        var act = () => sut.RememberAsync(Record());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecallAsync_ReturnsEmpty()
    {
        var sut = new RemoteCrossSessionMemoryStore();

        var result = await sut.RecallAsync(new MemoryQuery { Query = "anything" });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteCrossSessionMemoryStore();

        var act = () => sut.ForgetAsync("mem-1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PurgeByOwnerAsync_ThrowsInsteadOfClaimingZeroPurged()
    {
        // A silent 0 here would let DefaultErasureOrchestrator certify a right-to-erasure request
        // as Full while the subject's facts remain in the remote store — see the class remarks.
        var sut = new RemoteCrossSessionMemoryStore();

        var act = () => sut.PurgeByOwnerAsync("user-1");

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [Fact]
    public async Task ImproveAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteCrossSessionMemoryStore();

        var act = () => sut.ImproveAsync("mem-1", 0.1);

        await act.Should().NotThrowAsync();
    }
}
