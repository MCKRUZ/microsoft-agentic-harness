using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// <see cref="RemoteMemoryConsolidator"/> is a local, network-free stand-in — the remote memory
/// contract has no consolidation endpoint. It always decides "create new", the interface's own
/// documented safe default, regardless of what similar-existing candidates it is handed.
/// </summary>
public sealed class RemoteMemoryConsolidatorTests
{
    [Fact]
    public async Task ConsolidateAsync_NoSimilarExisting_DecidesCreate()
    {
        var sut = new RemoteMemoryConsolidator();

        var decision = await sut.ConsolidateAsync(
            new MemoryAbstraction { Abstraction = "candidate" }, "candidate value", []);

        decision.Action.Should().Be(ConsolidationAction.Create);
        decision.TargetId.Should().BeNull();
    }

    [Fact]
    public async Task ConsolidateAsync_SimilarExistingPresent_StillDecidesCreate()
    {
        // The remote contract has no consolidation endpoint to consult, so even a non-empty
        // similar-existing list cannot change the outcome — this is a safe default, not a real
        // similarity judgment.
        var sut = new RemoteMemoryConsolidator();
        var similar = new ExistingMemory
        {
            Id = "existing-1",
            Abstraction = "candidate",
            Value = "existing value",
        };

        var decision = await sut.ConsolidateAsync(
            new MemoryAbstraction { Abstraction = "candidate" }, "candidate value", [similar]);

        decision.Action.Should().Be(ConsolidationAction.Create);
    }
}
