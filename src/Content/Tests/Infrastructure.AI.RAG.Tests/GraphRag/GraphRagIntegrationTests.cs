using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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
