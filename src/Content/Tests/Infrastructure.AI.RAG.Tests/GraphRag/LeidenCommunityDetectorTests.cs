using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Integration tests for <see cref="LeidenCommunityDetector"/> using a real
/// <see cref="KuzuGraphBackend"/> with a temporary on-disk SQLite database.
/// Each test gets a fresh database; the temp directory is cleaned up on dispose.
/// </summary>
public sealed class LeidenCommunityDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KuzuGraphBackend _graph;
    private readonly LeidenCommunityDetector _sut;

    /// <summary>Creates a fresh temp directory, backend, and detector for each test.</summary>
    public LeidenCommunityDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"leiden_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _graph = new KuzuGraphBackend(_tempDir, NullLogger<KuzuGraphBackend>.Instance);
        _sut = new LeidenCommunityDetector(NullLogger<LeidenCommunityDetector>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _graph.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A small graph with two clusters separated by a bridge edge should assign all
    /// nodes to communities. Cluster A: n1-n2-n3; cluster B: n4-n5; bridge: n3→n4.
    /// </summary>
    [Fact]
    public async Task DetectAsync_SmallGraph_ReturnsExpectedCommunities()
    {
        // Arrange — two clusters connected by a single bridge edge
        var nodes = new[]
        {
            RagTestData.CreateGraphNode("n1", "Node1", "T"),
            RagTestData.CreateGraphNode("n2", "Node2", "T"),
            RagTestData.CreateGraphNode("n3", "Node3", "T"),
            RagTestData.CreateGraphNode("n4", "Node4", "T"),
            RagTestData.CreateGraphNode("n5", "Node5", "T"),
        };
        var edges = new[]
        {
            RagTestData.CreateGraphEdge("e1", "n1", "n2", "rel"),
            RagTestData.CreateGraphEdge("e2", "n2", "n3", "rel"),
            RagTestData.CreateGraphEdge("e3", "n1", "n3", "rel"),
            RagTestData.CreateGraphEdge("e4", "n4", "n5", "rel"),
            RagTestData.CreateGraphEdge("e5", "n3", "n4", "bridge"),
        };

        await _graph.AddNodesAsync(nodes);
        await _graph.AddEdgesAsync(edges);

        // Act
        var communities = await _sut.DetectAsync(_graph, targetLevels: 1);

        // Assert — all 5 nodes must be assigned to some community
        var allAssigned = communities.SelectMany(c => c.NodeIds).ToHashSet();
        Assert.Equal(5, allAssigned.Count);
        Assert.Contains("n1", allAssigned);
        Assert.Contains("n2", allAssigned);
        Assert.Contains("n3", allAssigned);
        Assert.Contains("n4", allAssigned);
        Assert.Contains("n5", allAssigned);
    }

    /// <summary>
    /// Two completely disconnected pairs of nodes should produce at least two communities,
    /// with n1 and n3 in different communities.
    /// </summary>
    [Fact]
    public async Task DetectAsync_DisconnectedComponents_SeparateCommunities()
    {
        // Arrange — pair A: n1-n2, pair B: n3-n4 (no edges between pairs)
        var nodes = new[]
        {
            RagTestData.CreateGraphNode("n1", "Node1", "T"),
            RagTestData.CreateGraphNode("n2", "Node2", "T"),
            RagTestData.CreateGraphNode("n3", "Node3", "T"),
            RagTestData.CreateGraphNode("n4", "Node4", "T"),
        };
        var edges = new[]
        {
            RagTestData.CreateGraphEdge("e1", "n1", "n2", "rel"),
            RagTestData.CreateGraphEdge("e2", "n3", "n4", "rel"),
        };

        await _graph.AddNodesAsync(nodes);
        await _graph.AddEdgesAsync(edges);

        // Act
        var communities = await _sut.DetectAsync(_graph, targetLevels: 1);

        // Assert — at least 2 communities, n1 and n3 in different ones
        var level0 = communities.Where(c => c.Level == 0).ToList();
        Assert.True(level0.Count >= 2, $"Expected >= 2 communities at level 0, got {level0.Count}");

        var communityOfN1 = level0.Single(c => c.NodeIds.Contains("n1")).Id;
        var communityOfN3 = level0.Single(c => c.NodeIds.Contains("n3")).Id;
        Assert.NotEqual(communityOfN1, communityOfN3);
    }

    /// <summary>
    /// Requesting 2 hierarchy levels on a graph with two clusters should produce communities
    /// at both level 0 (granular) and level 1 (merged), with level 1 having fewer or equal
    /// communities than level 0.
    /// </summary>
    [Fact]
    public async Task DetectAsync_MultipleLevels_ReturnsHierarchy()
    {
        // Arrange — two clusters (n1-n2-n3) and (n4-n5-n6) with a bridge
        var nodes = Enumerable.Range(1, 6)
            .Select(i => RagTestData.CreateGraphNode($"n{i}", $"Node{i}", "T"))
            .ToArray();
        var edges = new[]
        {
            RagTestData.CreateGraphEdge("e1", "n1", "n2", "rel"),
            RagTestData.CreateGraphEdge("e2", "n2", "n3", "rel"),
            RagTestData.CreateGraphEdge("e3", "n1", "n3", "rel"),
            RagTestData.CreateGraphEdge("e4", "n4", "n5", "rel"),
            RagTestData.CreateGraphEdge("e5", "n5", "n6", "rel"),
            RagTestData.CreateGraphEdge("e6", "n4", "n6", "rel"),
            RagTestData.CreateGraphEdge("e7", "n3", "n4", "bridge"),
        };

        await _graph.AddNodesAsync(nodes);
        await _graph.AddEdgesAsync(edges);

        // Act
        var communities = await _sut.DetectAsync(_graph, targetLevels: 2);

        // Assert — communities at both levels exist, level 1 has <= level 0 count
        var level0Count = communities.Count(c => c.Level == 0);
        var level1Count = communities.Count(c => c.Level == 1);

        Assert.True(level0Count > 0, "Expected communities at level 0");
        Assert.True(level1Count > 0, "Expected communities at level 1");
        Assert.True(level1Count <= level0Count,
            $"Level 1 ({level1Count}) should have <= communities than level 0 ({level0Count})");
    }

    /// <summary>
    /// A graph with a single node should return exactly one community containing that node.
    /// </summary>
    [Fact]
    public async Task DetectAsync_SingleNode_ReturnsSingleCommunity()
    {
        // Arrange
        await _graph.AddNodesAsync([RagTestData.CreateGraphNode("n1", "Lone", "T")]);

        // Act
        var communities = await _sut.DetectAsync(_graph, targetLevels: 1);

        // Assert
        var level0 = communities.Where(c => c.Level == 0).ToList();
        Assert.Single(level0);
        Assert.Contains("n1", level0[0].NodeIds);
    }

    /// <summary>
    /// An empty graph should return an empty community list without throwing.
    /// </summary>
    [Fact]
    public async Task DetectAsync_EmptyGraph_ReturnsEmpty()
    {
        // Act
        var communities = await _sut.DetectAsync(_graph, targetLevels: 1);

        // Assert
        Assert.Empty(communities);
    }

    /// <summary>
    /// A pre-cancelled token should cause DetectAsync to throw <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task DetectAsync_CancellationRequested_Throws()
    {
        // Arrange — add a node so cancellation check is reached after setup
        await _graph.AddNodesAsync([RagTestData.CreateGraphNode("n1", "Node1", "T")]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.DetectAsync(_graph, targetLevels: 1, cancellationToken: cts.Token));
    }
}
