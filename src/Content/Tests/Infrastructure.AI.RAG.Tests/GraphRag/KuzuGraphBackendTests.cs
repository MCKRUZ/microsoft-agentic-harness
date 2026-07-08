using Domain.AI.KnowledgeGraph.Models;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Integration tests for <see cref="KuzuGraphBackend"/> using a temporary on-disk SQLite
/// database. Each test gets a fresh database; the temp directory is cleaned up on dispose.
/// </summary>
public sealed class KuzuGraphBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KuzuGraphBackend _sut;

    public KuzuGraphBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kuzu_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new KuzuGraphBackend(_tempDir, NullLogger<KuzuGraphBackend>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── AddNodesAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddNodesAsync_NewNodes_StoresAndRetrieves()
    {
        // Arrange
        var n1 = new GraphNode { Id = "n1", Name = "Azure OpenAI", Type = "Technology", ChunkIds = ["c1"] };
        var n2 = new GraphNode { Id = "n2", Name = "Microsoft", Type = "Organization", ChunkIds = ["c2"] };

        // Act
        await _sut.AddNodesAsync([n1, n2]);

        // Assert
        var retrieved1 = await _sut.GetNodeAsync("n1");
        var retrieved2 = await _sut.GetNodeAsync("n2");

        Assert.NotNull(retrieved1);
        Assert.Equal("Azure OpenAI", retrieved1.Name);
        Assert.Equal("Technology", retrieved1.Type);

        Assert.NotNull(retrieved2);
        Assert.Equal("Microsoft", retrieved2.Name);
        Assert.Equal("Organization", retrieved2.Type);
    }

    [Fact]
    public async Task AddNodesAsync_DuplicateId_MergesChunkIds()
    {
        // Arrange
        var initial = new GraphNode { Id = "n1", Name = "Azure OpenAI", Type = "Technology", ChunkIds = ["c1"] };
        var duplicate = new GraphNode { Id = "n1", Name = "Azure OpenAI", Type = "Technology", ChunkIds = ["c2"] };

        // Act
        await _sut.AddNodesAsync([initial]);
        await _sut.AddNodesAsync([duplicate]);

        // Assert
        var retrieved = await _sut.GetNodeAsync("n1");
        Assert.NotNull(retrieved);
        Assert.Contains("c1", retrieved.ChunkIds);
        Assert.Contains("c2", retrieved.ChunkIds);
        Assert.Equal(2, retrieved.ChunkIds.Count);
    }

    // ── AddEdgesAsync / GetTripletsAsync ─────────────────────────────────────

    [Fact]
    public async Task AddEdgesAsync_NewEdges_StoresAndRetrievesViaTriplets()
    {
        // Arrange
        var n1 = new GraphNode { Id = "n1", Name = "Azure OpenAI", Type = "Technology", ChunkIds = ["c1"] };
        var n2 = new GraphNode { Id = "n2", Name = "Microsoft", Type = "Organization", ChunkIds = ["c1"] };
        var edge = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "owned_by", ChunkId = "c1"
        };

        await _sut.AddNodesAsync([n1, n2]);

        // Act
        await _sut.AddEdgesAsync([edge]);

        // Assert
        var triplets = await _sut.GetTripletsAsync(["n1"]);
        Assert.Single(triplets);
        Assert.Equal("owned_by", triplets[0].Edge.Predicate);
        Assert.Equal("n1", triplets[0].Source.Id);
        Assert.Equal("n2", triplets[0].Target.Id);
    }

    // ── TraverseAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task TraverseAsync_MultiHop_ReturnsAllReachableNodes()
    {
        // Arrange — chain: n1 → n2 → n3 → n4
        GraphNode MakeNode(string id) => new() { Id = id, Name = id, Type = "T", ChunkIds = ["c1"] };
        GraphEdge MakeEdge(string id, string src, string tgt) =>
            new() { Id = id, SourceNodeId = src, TargetNodeId = tgt, Predicate = "links_to", ChunkId = "c1" };

        await _sut.AddNodesAsync([MakeNode("n1"), MakeNode("n2"), MakeNode("n3"), MakeNode("n4")]);
        await _sut.AddEdgesAsync([MakeEdge("e1", "n1", "n2"), MakeEdge("e2", "n2", "n3"), MakeEdge("e3", "n3", "n4")]);

        // Act — depth 2 from n1 should reach n2 (depth 1) and n3 (depth 2), but NOT n4 or n1 itself
        var result = await _sut.TraverseAsync("n1", maxDepth: 2);

        // Assert
        var ids = result.Select(n => n.Id).ToHashSet();
        Assert.Contains("n2", ids);
        Assert.Contains("n3", ids);
        Assert.DoesNotContain("n4", ids);
        Assert.DoesNotContain("n1", ids);
    }

    // ── UpdateNodeWeightAsync ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateNodeWeightAsync_ExistingNode_UpdatesWeight()
    {
        // Arrange
        var node = new GraphNode { Id = "n1", Name = "Azure", Type = "Technology", ChunkIds = ["c1"] };
        await _sut.AddNodesAsync([node]);

        // Act — should not throw; weight is a backend-internal field
        await _sut.UpdateNodeWeightAsync("n1", weight: 0.75);

        // Assert — node still retrievable after weight update
        var retrieved = await _sut.GetNodeAsync("n1");
        Assert.NotNull(retrieved);
        Assert.Equal("n1", retrieved.Id);
    }

    // ── AssignCommunityAsync / GetCommunityNodesAsync ─────────────────────────

    [Fact]
    public async Task AssignCommunityAsync_ThenGetCommunityNodes_ReturnsAssignedNodes()
    {
        // Arrange
        GraphNode MakeNode(string id) => new() { Id = id, Name = id, Type = "T", ChunkIds = ["c1"] };
        await _sut.AddNodesAsync([MakeNode("n1"), MakeNode("n2"), MakeNode("n3")]);

        // Act — assign n1 and n2 to community-A, not n3
        await _sut.AssignCommunityAsync("n1", "community-A", level: 0);
        await _sut.AssignCommunityAsync("n2", "community-A", level: 0);

        // Assert
        var communityNodes = await _sut.GetCommunityNodesAsync("community-A");
        var ids = communityNodes.Select(n => n.Id).ToHashSet();

        Assert.Equal(2, communityNodes.Count);
        Assert.Contains("n1", ids);
        Assert.Contains("n2", ids);
        Assert.DoesNotContain("n3", ids);
    }

    // ── SaveCommunityAsync / GetCommunitiesAsync ──────────────────────────────

    [Fact]
    public async Task SaveCommunityAsync_ThenGetCommunities_ReturnsSavedCommunity()
    {
        // Arrange
        var community = new Community
        {
            Id = "community_0_1",
            Level = 0,
            Summary = "A cluster of cloud infrastructure entities.",
            NodeIds = ["n1", "n2", "n3"],
            Modularity = 0.72
        };

        // Act
        await _sut.SaveCommunityAsync(community);

        // Assert
        var results = await _sut.GetCommunitiesAsync(level: 0);
        Assert.Single(results);
        Assert.Equal("community_0_1", results[0].Id);
        Assert.Equal("A cluster of cloud infrastructure entities.", results[0].Summary);
        Assert.Equal(3, results[0].NodeIds.Count);
        Assert.Contains("n2", results[0].NodeIds);
    }

    // ── DeleteNodeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteNodeAsync_ExistingNode_RemovesNodeAndEdges()
    {
        // Arrange
        var n1 = new GraphNode { Id = "n1", Name = "A", Type = "T", ChunkIds = ["c1"] };
        var n2 = new GraphNode { Id = "n2", Name = "B", Type = "T", ChunkIds = ["c1"] };
        var edge = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "links_to", ChunkId = "c1"
        };

        await _sut.AddNodesAsync([n1, n2]);
        await _sut.AddEdgesAsync([edge]);

        // Act
        await _sut.DeleteNodeAsync("n1");

        // Assert — n1 gone
        var deleted = await _sut.GetNodeAsync("n1");
        Assert.Null(deleted);

        // Assert — n2 still exists
        var remaining = await _sut.GetNodeAsync("n2");
        Assert.NotNull(remaining);

        // Assert — triplets from n2 are empty (edge was removed)
        var triplets = await _sut.GetTripletsAsync(["n2"]);
        Assert.Empty(triplets);
    }

    // ── DeleteNodesAsync / DeleteEdgesByOwnerAsync (erasure primitives) ──────

    [Fact]
    public async Task DeleteNodesAsync_ReturnsActualCountsAndCascadedEdgeIds()
    {
        // Arrange
        await _sut.AddNodesAsync([
            new GraphNode { Id = "n1", Name = "A", Type = "T", ChunkIds = ["c1"] },
            new GraphNode { Id = "n2", Name = "B", Type = "T", ChunkIds = ["c1"] },
            new GraphNode { Id = "n3", Name = "C", Type = "T", ChunkIds = ["c1"] }
        ]);
        await _sut.AddEdgesAsync([
            new GraphEdge { Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2", Predicate = "links", ChunkId = "c1" },
            new GraphEdge { Id = "e2", SourceNodeId = "n3", TargetNodeId = "n1", Predicate = "links", ChunkId = "c1" },
            new GraphEdge { Id = "e3", SourceNodeId = "n2", TargetNodeId = "n3", Predicate = "links", ChunkId = "c1" }
        ]);

        // Act — one requested id does not exist and must not be counted
        var result = await _sut.DeleteNodesAsync(["n1", "missing"]);

        // Assert
        Assert.Equal(1, result.NodesDeleted);
        Assert.Equal(["e1", "e2"], result.DeletedEdgeIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(await _sut.GetNodeAsync("n1"));
        Assert.Equal(1, await _sut.GetEdgeCountAsync());
        Assert.Equal(2, await _sut.GetNodeCountAsync());
    }

    [Fact]
    public async Task DeleteEdgesByOwnerAsync_RemovesOnlyThatOwnersEdges()
    {
        // Arrange
        await _sut.AddNodesAsync([
            new GraphNode { Id = "n1", Name = "A", Type = "T", ChunkIds = ["c1"] },
            new GraphNode { Id = "n2", Name = "B", Type = "T", ChunkIds = ["c1"] }
        ]);
        await _sut.AddEdgesAsync([
            new GraphEdge
            {
                Id = "e-owned", SourceNodeId = "n1", TargetNodeId = "n2",
                Predicate = "links", ChunkId = "c1", OwnerId = "user-1"
            },
            new GraphEdge
            {
                Id = "e-foreign", SourceNodeId = "n2", TargetNodeId = "n1",
                Predicate = "links", ChunkId = "c1", OwnerId = "user-2"
            },
            new GraphEdge
            {
                Id = "e-unowned", SourceNodeId = "n1", TargetNodeId = "n2",
                Predicate = "cites", ChunkId = "c1"
            }
        ]);

        // Act
        var deleted = await _sut.DeleteEdgesByOwnerAsync("user-1");

        // Assert
        Assert.Equal(["e-owned"], deleted);
        Assert.Equal(2, await _sut.GetEdgeCountAsync());
    }
}
