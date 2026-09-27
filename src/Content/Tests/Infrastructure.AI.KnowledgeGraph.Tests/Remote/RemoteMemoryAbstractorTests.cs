using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// <see cref="RemoteMemoryAbstractor"/> is a local, network-free stand-in — the remote memory
/// contract has no abstraction-generation endpoint. These pin the safe-default contract: no
/// exception, no cue anchors, the candidate content echoed back as the abstraction.
/// </summary>
public sealed class RemoteMemoryAbstractorTests
{
    [Fact]
    public async Task AbstractAsync_ReturnsContentAsAbstraction_WithNoCueAnchors()
    {
        var sut = new RemoteMemoryAbstractor();

        var result = await sut.AbstractAsync("candidate memory content");

        result.Abstraction.Should().Be("candidate memory content");
        result.CueAnchors.Should().BeEmpty();
    }
}
