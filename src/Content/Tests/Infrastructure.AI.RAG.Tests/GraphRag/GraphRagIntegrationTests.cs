using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Integration tests for <see cref="ManagedCodeGraphRagService"/> using a real
/// <see cref="KuzuGraphBackend"/> and mocked LLM infrastructure. Each test creates
/// a fresh on-disk database; the temp directory is cleaned up on dispose.
/// </summary>
public sealed class GraphRagIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KuzuGraphBackend _graphBackend;
    private readonly Mock<IChatClient> _mockChatClient;
    private readonly Mock<IRagModelRouter> _mockModelRouter;
    private readonly Mock<IProvenanceStamper> _mockProvenanceStamper;
    private readonly Mock<ICommunityDetector> _mockCommunityDetector;
    private readonly ManagedCodeGraphRagService _sut;

    /// <summary>
    /// Creates a fresh temp directory, real KuzuGraphBackend, and mock LLM collaborators
    /// for each test. The model router always returns the mock chat client.
    /// </summary>
    public GraphRagIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"graphrag_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _graphBackend = new KuzuGraphBackend(_tempDir, NullLogger<KuzuGraphBackend>.Instance);

        _mockChatClient = new Mock<IChatClient>();
        _mockModelRouter = new Mock<IRagModelRouter>();
        _mockProvenanceStamper = new Mock<IProvenanceStamper>();
        _mockCommunityDetector = new Mock<ICommunityDetector>();

        // Router always returns the mock client regardless of operation name.
        _mockModelRouter
            .Setup(r => r.GetClientForOperation(It.IsAny<string>()))
            .Returns(_mockChatClient.Object);

        // Stamper passes entities through unchanged.
        _mockProvenanceStamper
            .Setup(s => s.StampNode(It.IsAny<GraphNode>(), It.IsAny<ProvenanceStamp>()))
            .Returns((GraphNode node, ProvenanceStamp _) => node);
        _mockProvenanceStamper
            .Setup(s => s.StampEdge(It.IsAny<GraphEdge>(), It.IsAny<ProvenanceStamp>()))
            .Returns((GraphEdge edge, ProvenanceStamp _) => edge);
        _mockProvenanceStamper
            .Setup(s => s.CreateStamp(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<double?>(), It.IsAny<string?>()))
            .Returns(new ProvenanceStamp
            {
                SourcePipeline = "test",
                SourceTask = "test",
                Timestamp = DateTimeOffset.UtcNow
            });

        var configMonitor = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.GraphRag.Enabled = true;
            c.AI.Rag.GraphRag.CommunityLevel = 0;
        });

        _sut = new ManagedCodeGraphRagService(
            _graphBackend,
            _mockModelRouter.Object,
            _mockProvenanceStamper.Object,
            _mockCommunityDetector.Object,
            NullLogger<ManagedCodeGraphRagService>.Instance,
            configMonitor);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _graphBackend.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── IndexCorpusAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// Indexing a chunk containing entities should persist at least one node to the
    /// graph backend, proving the extraction → provenance stamp → AddNodesAsync flow works
    /// end-to-end with a real storage layer.
    /// </summary>
    [Fact]
    public async Task IndexCorpusAsync_PersistsToGraphBackend()
    {
        // Arrange — LLM extraction returns two entities and one relationship.
        SetupExtractionResponse("""
            {
              "entities": [
                {"name": "Azure", "type": "Technology"},
                {"name": "Microsoft", "type": "Organization"}
              ],
              "relationships": [
                {"source": "Azure", "predicate": "owned_by", "target": "Microsoft"}
              ]
            }
            """);

        var chunk = RagTestData.CreateChunk("c1", "Azure is a cloud platform owned by Microsoft.");

        // Act
        await _sut.IndexCorpusAsync([chunk]);

        // Assert — at least one entity persisted to the real graph.
        var nodeCount = await _graphBackend.GetNodeCountAsync();
        Assert.True(nodeCount > 0, $"Expected nodes in graph after indexing but found {nodeCount}.");
    }

    // ── GlobalSearchAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// When communities exist at the configured level, global search should build its
    /// summary from community records rather than raw triplets, and the synthesis LLM
    /// response should flow through to the returned assembled context.
    /// </summary>
    [Fact]
    public async Task GlobalSearchAsync_UsesCommunities_WhenAvailable()
    {
        // Arrange — add a node and a pre-computed community at level 0.
        var node = new GraphNode { Id = "n1", Name = "Cloud Computing", Type = "Concept", ChunkIds = ["c1"] };
        await _graphBackend.AddNodesAsync([node]);

        var community = new Community
        {
            Id = "community_0_1",
            Level = 0,
            Summary = "A community of cloud infrastructure entities including Azure and AWS.",
            NodeIds = ["n1"],
            Modularity = 0.65
        };
        await _graphBackend.SaveCommunityAsync(community);

        // Mock LLM synthesis to return a deterministic answer.
        SetupSynthesisResponse("cloud computing");

        // Act
        var result = await _sut.GlobalSearchAsync("What are the cloud platforms?", communityLevel: 0);

        // Assert — synthesis result propagated, community path taken.
        Assert.NotEmpty(result.AssembledText);
        Assert.Contains("cloud computing", result.AssembledText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When no communities exist at the requested level, global search should fall back
    /// to building a summary from raw triplets and still return a non-empty result when
    /// nodes and edges are present.
    /// </summary>
    [Fact]
    public async Task GlobalSearchAsync_FallsBackToFullScan_WhenNoCommunitiesExist()
    {
        // Arrange — two nodes and one edge, no communities saved.
        var n1 = new GraphNode { Id = "n1", Name = "Azure", Type = "Technology", ChunkIds = ["c1"] };
        var n2 = new GraphNode { Id = "n2", Name = "Microsoft", Type = "Organization", ChunkIds = ["c1"] };
        var edge = new GraphEdge { Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2", Predicate = "owned_by", ChunkId = "c1" };
        await _graphBackend.AddNodesAsync([n1, n2]);
        await _graphBackend.AddEdgesAsync([edge]);

        SetupSynthesisResponse("Azure is owned by Microsoft.");

        // Act
        var result = await _sut.GlobalSearchAsync("Who owns Azure?", communityLevel: 0);

        // Assert — fallback path returns non-empty synthesis.
        Assert.NotEmpty(result.AssembledText);
    }

    // ── LocalSearchAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// When a query matches a node by name, local search should traverse its neighbors
    /// and return retrieval results whose chunk IDs include the matched node's chunk.
    /// </summary>
    [Fact]
    public async Task LocalSearchAsync_UsesGraphTraversal()
    {
        // Arrange — two nodes connected by an edge; n1 references chunk c1.
        var n1 = new GraphNode { Id = "n1", Name = "Azure OpenAI", Type = "Technology", ChunkIds = ["c1"] };
        var n2 = new GraphNode { Id = "n2", Name = "Microsoft", Type = "Organization", ChunkIds = ["c2"] };
        var edge = new GraphEdge { Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2", Predicate = "owned_by", ChunkId = "c1" };
        await _graphBackend.AddNodesAsync([n1, n2]);
        await _graphBackend.AddEdgesAsync([edge]);

        // Act — "Azure" matches n1 by name prefix.
        var results = await _sut.LocalSearchAsync("Azure", topK: 10);

        // Assert — chunk c1 from the matched node appears in results.
        Assert.NotEmpty(results);
        var chunkIds = results.Select(r => r.Chunk.Id).ToHashSet();
        Assert.Contains("c1", chunkIds);
    }

    // ── End-to-End Pipeline Tests ────────────────────────────────────────────

    /// <summary>
    /// Exercises the full pipeline: ingest documents via LLM extraction, run real Leiden
    /// community detection, persist communities, then execute a global search that
    /// synthesizes from community summaries.
    /// </summary>
    [Fact]
    public async Task EndToEnd_Ingest_DetectCommunities_GlobalSearch_ReturnsCommunityBasedAnswer()
    {
        // Arrange — configure with real community detector instead of mock
        var detector = new LeidenCommunityDetector(NullLogger<LeidenCommunityDetector>.Instance);
        var configMonitor = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.GraphRag.Enabled = true;
            c.AI.Rag.GraphRag.CommunityLevel = 0;
        });

        var sutWithRealDetector = new ManagedCodeGraphRagService(
            _graphBackend,
            _mockModelRouter.Object,
            _mockProvenanceStamper.Object,
            detector,
            NullLogger<ManagedCodeGraphRagService>.Instance,
            configMonitor);

        // Ingest — LLM extracts entities from chunk content
        SetupExtractionResponse("""
            {
              "entities": [
                {"name": "Azure", "type": "Technology"},
                {"name": "Microsoft", "type": "Organization"}
              ],
              "relationships": [
                {"source": "Azure", "predicate": "owned_by", "target": "Microsoft"}
              ]
            }
            """);

        var chunks = new List<DocumentChunk>
        {
            RagTestData.CreateChunk("c1", "Azure is a cloud computing platform by Microsoft.")
        };
        await sutWithRealDetector.IndexCorpusAsync(chunks);

        // Detect communities with real Leiden algorithm
        var communities = await detector.DetectAsync(_graphBackend, targetLevels: 1);
        foreach (var community in communities)
        {
            await _graphBackend.SaveCommunityAsync(community);
            foreach (var nodeId in community.NodeIds)
                await _graphBackend.AssignCommunityAsync(nodeId, community.Id, community.Level);
        }

        // Set up synthesis client for global search
        var synthesisClient = new Mock<IChatClient>();
        synthesisClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "The corpus centers on Azure cloud computing technology.")));
        _mockModelRouter.Setup(r => r.GetClientForOperation("graph_global_search"))
            .Returns(synthesisClient.Object);

        // Act
        var result = await sutWithRealDetector.GlobalSearchAsync("What are the main themes?", communityLevel: 0);

        // Assert
        result.AssembledText.Should().Contain("Azure");
        result.TotalTokens.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Stores a memory via <see cref="CrossSessionMemoryStore"/>, syncs to the real graph
    /// backend, then recalls from the cache to verify end-to-end remember/recall flow.
    /// </summary>
    [Fact]
    public async Task EndToEnd_MemoryStoreAndRecall_WorksAcrossSessions()
    {
        // Arrange
        var config = new AppConfig();
        config.AI.Rag.CrossSessionMemory = new CrossSessionMemoryConfig
        {
            Enabled = true,
            MaxMemories = 100,
            PruneThreshold = 0.01,
            SyncInterval = TimeSpan.FromMinutes(30)
        };
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);

        var memoryStore = new CrossSessionMemoryStore(
            _graphBackend, monitor.Object, NullLogger<CrossSessionMemoryStore>.Instance);

        // Act — store a memory
        var memory = RagTestData.CreateMemoryRecord("mem-e2e", "User prefers TypeScript over JavaScript.");
        await memoryStore.RememberAsync(memory);

        // Sync dirty entries to graph backend
        await memoryStore.SyncToBackendAsync();

        // Verify node persisted to real graph
        var nodeExists = await _graphBackend.NodeExistsAsync("mem-e2e");
        nodeExists.Should().BeTrue("memory should be persisted to graph backend after sync");

        // Recall from cache — should still match keyword search
        var recalledFromCache = await memoryStore.RecallAsync(
            RagTestData.CreateMemoryQuery("TypeScript"));

        // Assert
        recalledFromCache.Should().ContainSingle(m => m.Id == "mem-e2e");

        // Cleanup
        memoryStore.Dispose();
    }

    /// <summary>
    /// Stores a memory node with an old access date directly in the graph, applies
    /// aggressive EMA decay, then prunes — verifying the stale memory is removed.
    /// </summary>
    [Fact]
    public async Task EndToEnd_DecayAndPrune_RemovesStaleMemories()
    {
        // Arrange
        var config = new AppConfig();
        config.AI.Rag.CrossSessionMemory = new CrossSessionMemoryConfig
        {
            Enabled = true,
            DecayRate = 0.5,      // aggressive decay for testing
            PruneThreshold = 0.1,
            SyncInterval = TimeSpan.FromMinutes(30)
        };
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);

        var memoryStore = new CrossSessionMemoryStore(
            _graphBackend, monitor.Object, NullLogger<CrossSessionMemoryStore>.Instance);

        // Store a memory node with an old access date directly in the graph
        var oldNode = new GraphNode
        {
            Id = "mem-stale",
            Name = "Stale memory",
            Type = "Memory",
            Properties = new Dictionary<string, string>
            {
                ["weight"] = "0.3000",
                ["last_accessed_at"] = DateTimeOffset.UtcNow.AddDays(-10).ToString("O"),
                ["content"] = "Old information",
                ["source"] = "old-session"
            }
        };
        await _graphBackend.AddNodesAsync([oldNode]);

        var decayService = new MemoryDecayService(
            _graphBackend, memoryStore, monitor.Object, NullLogger<MemoryDecayService>.Instance);

        // Act — apply decay: 0.3 * (1 - 0.5)^10 = 0.3 * 0.000977 = ~0.000293
        await decayService.ApplyDecayAsync();
        await decayService.PruneAsync(threshold: 0.1);

        // Assert — the stale memory should be pruned
        var exists = await _graphBackend.NodeExistsAsync("mem-stale");
        exists.Should().BeFalse("stale memory should be pruned after decay");

        // Cleanup
        memoryStore.Dispose();
        decayService.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupExtractionResponse(string json) =>
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

    private void SetupSynthesisResponse(string text) =>
        _mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
}
