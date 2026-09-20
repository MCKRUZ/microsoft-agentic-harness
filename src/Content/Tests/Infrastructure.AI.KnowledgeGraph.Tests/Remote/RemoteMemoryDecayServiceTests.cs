using FluentAssertions;
using Infrastructure.AI.RAG.Remote;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// <see cref="RemoteMemoryDecayService"/> is a local, network-free stand-in — the remote memory
/// contract's <c>/prune</c> endpoint is deliberately inert for this phase of the avatar-hosting
/// migration, so both methods are safe no-ops.
/// </summary>
public sealed class RemoteMemoryDecayServiceTests
{
    [Fact]
    public async Task ApplyDecayAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteMemoryDecayService();

        var act = () => sut.ApplyDecayAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PruneAsync_CompletesWithoutThrowing()
    {
        var sut = new RemoteMemoryDecayService();

        var act = () => sut.PruneAsync(0.5);

        await act.Should().NotThrowAsync();
    }
}
