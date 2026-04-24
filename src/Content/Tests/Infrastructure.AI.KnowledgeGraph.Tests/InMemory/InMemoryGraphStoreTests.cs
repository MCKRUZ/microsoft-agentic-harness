using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.InMemory;

/// <summary>
/// Tests for <see cref="InMemoryGraphStore"/> — CRUD operations, neighbor traversal,
/// triplet retrieval, node merging, and edge deduplication.
/// </summary>
public sealed class InMemoryGraphStoreTests
{
    private readonly InMemoryGraphStore _store;

    public InMemoryGraphStoreTests()
    {
        _store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
    }

    [Fact]
    public async Task AddNodes_SingleNode_CanBeRetrieved()
    {
        var node = CreateNode("n1", "Azure", "Technology");
        await _store.AddNodesAsync([node]);

        var retrieved = await _store.GetNodeAsync("n1");
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Azure");
        retrieved.Type.Should().Be("Technology");
    }

    [Fact]
    public async Task AddNodes_DuplicateId_MergesChunkIds()
    {
        var node1 = CreateNode("n1", "Azure", "Technology", chunkIds: ["c1"]);
        var node2 = CreateNode("n1", "Azure", "Technology", chunkIds: ["c2"]);

        await _store.AddNodesAsync([node1]);
        await _store.AddNodesAsync([node2]);

        var retrieved = await _store.GetNodeAsync("n1");
        retrieved!.ChunkIds.Should().BeEquivalentTo(["c1", "c2"]);
    }

    [Fact]
    public async Task AddNodes_DuplicateId_MergesProperties()
    {
        var node1 = new GraphNode
        {
            Id = "n1", Name = "Test", Type = "Entity",
            Properties = new Dictionary<string, string> { ["key1"] = "val1" }
        };
        var node2 = new GraphNode
        {
            Id = "n1", Name = "Test", Type = "Entity",
            Properties = new Dictionary<string, string> { ["key2"] = "val2" }
        };

        await _store.AddNodesAsync([node1]);
        await _store.AddNodesAsync([node2]);

        var retrieved = await _store.GetNodeAsync("n1");
        retrieved!.Properties.Should().ContainKey("key1");
        retrieved.Properties.Should().ContainKey("key2");
    }

    [Fact]
    public async Task GetNode_NonExistent_ReturnsNull()
    {
        var result = await _store.GetNodeAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddEdges_SingleEdge_CanBeCountedViaGetEdgeCount()
    {
        await SeedGraphAsync();
        var count = await _store.GetEdgeCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task AddEdges_DuplicateId_SilentlyIgnored()
    {
        await SeedGraphAsync();
        var duplicateEdge = CreateEdge("e1", "n1", "n2", "uses", "c1");
        await _store.AddEdgesAsync([duplicateEdge]);

        var count = await _store.GetEdgeCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task GetNeighbors_Depth1_ReturnsDirectNeighbors()
    {
        await SeedGraphAsync();
        var neighbors = await _store.GetNeighborsAsync("n1", maxDepth: 1);

        neighbors.Should().HaveCount(1);
        neighbors[0].Id.Should().Be("n2");
    }

    [Fact]
    public async Task GetNeighbors_Depth2_ReturnsTransitiveNeighbors()
    {
        var n1 = CreateNode("n1", "A", "Entity");
        var n2 = CreateNode("n2", "B", "Entity");
        var n3 = CreateNode("n3", "C", "Entity");
        await _store.AddNodesAsync([n1, n2, n3]);

        var e1 = CreateEdge("e1", "n1", "n2", "links", "c1");
        var e2 = CreateEdge("e2", "n2", "n3", "links", "c1");
        await _store.AddEdgesAsync([e1, e2]);

        var neighbors = await _store.GetNeighborsAsync("n1", maxDepth: 2);
        neighbors.Should().HaveCount(2);
        neighbors.Select(n => n.Id).Should().BeEquivalentTo(["n2", "n3"]);
    }

    [Fact]
    public async Task GetNeighbors_NoEdges_ReturnsEmpty()
    {
        await _store.AddNodesAsync([CreateNode("n1", "Isolated", "Entity")]);
        var neighbors = await _store.GetNeighborsAsync("n1");
        neighbors.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTriplets_ReturnsMatchingTriplets()
    {
        await SeedGraphAsync();
        var triplets = await _store.GetTripletsAsync(["n1"]);

        triplets.Should().HaveCount(1);
        triplets[0].Source.Name.Should().Be("Azure");
        triplets[0].Edge.Predicate.Should().Be("uses");
        triplets[0].Target.Name.Should().Be("OpenAI");
    }

    [Fact]
    public async Task GetTriplets_EmptyNodeIds_ReturnsEmpty()
    {
        await SeedGraphAsync();
        var triplets = await _store.GetTripletsAsync([]);
        triplets.Should().BeEmpty();
    }

    [Fact]
    public async Task NodeExists_ExistingNode_ReturnsTrue()
    {
        await _store.AddNodesAsync([CreateNode("n1", "Test", "Entity")]);
        var exists = await _store.NodeExistsAsync("n1");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task NodeExists_NonExistent_ReturnsFalse()
    {
        var exists = await _store.NodeExistsAsync("nonexistent");
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteNode_RemovesNodeAndConnectedEdges()
    {
        await SeedGraphAsync();
        await _store.DeleteNodeAsync("n1");

        (await _store.GetNodeAsync("n1")).Should().BeNull();
        (await _store.GetNodeCountAsync()).Should().Be(1);
        (await _store.GetEdgeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteNode_NonExistent_IsNoOp()
    {
        await SeedGraphAsync();
        await _store.DeleteNodeAsync("nonexistent");
        (await _store.GetNodeCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteEdge_RemovesEdgeOnly()
    {
        await SeedGraphAsync();
        await _store.DeleteEdgeAsync("e1");

        (await _store.GetEdgeCountAsync()).Should().Be(0);
        (await _store.GetNodeCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetNodeCount_ReturnsCorrectCount()
    {
        await SeedGraphAsync();
        var count = await _store.GetNodeCountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task GetEdgeCount_ReturnsCorrectCount()
    {
        await SeedGraphAsync();
        var count = await _store.GetEdgeCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task GetNeighbors_BidirectionalTraversal()
    {
        await SeedGraphAsync();
        var neighborsFromTarget = await _store.GetNeighborsAsync("n2", maxDepth: 1);

        neighborsFromTarget.Should().HaveCount(1);
        neighborsFromTarget[0].Id.Should().Be("n1");
    }

    private async Task SeedGraphAsync()
    {
        var n1 = CreateNode("n1", "Azure", "Technology", chunkIds: ["c1"]);
        var n2 = CreateNode("n2", "OpenAI", "Organization", chunkIds: ["c1"]);
        await _store.AddNodesAsync([n1, n2]);

        var edge = CreateEdge("e1", "n1", "n2", "uses", "c1");
        await _store.AddEdgesAsync([edge]);
    }

    private static GraphNode CreateNode(
        string id, string name, string type, string[]? chunkIds = null) =>
        new()
        {
            Id = id, Name = name, Type = type,
            ChunkIds = chunkIds ?? []
        };

    private static GraphEdge CreateEdge(
        string id, string sourceId, string targetId, string predicate, string chunkId) =>
        new()
        {
            Id = id, SourceNodeId = sourceId, TargetNodeId = targetId,
            Predicate = predicate, ChunkId = chunkId
        };
}
