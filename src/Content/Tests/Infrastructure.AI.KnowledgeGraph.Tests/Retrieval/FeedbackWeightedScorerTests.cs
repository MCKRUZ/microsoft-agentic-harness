using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Retrieval;

/// <summary>
/// Tests for <see cref="FeedbackWeightedScorer"/> — score blending,
/// no-match passthrough, and re-sorting behavior.
/// </summary>
public sealed class FeedbackWeightedScorerTests
{
    private readonly Mock<IFeedbackStore> _feedbackStore;
    private readonly Mock<IKnowledgeGraphStore> _graphStore;
    private readonly Mock<IOptionsMonitor<AppConfig>> _configMonitor;
    private readonly FeedbackWeightedScorer _scorer;

    public FeedbackWeightedScorerTests()
    {
        _feedbackStore = new Mock<IFeedbackStore>();
        _graphStore = new Mock<IKnowledgeGraphStore>();
        _configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        SetAlpha(0.3);

        _scorer = new FeedbackWeightedScorer(
            _feedbackStore.Object,
            _graphStore.Object,
            _configMonitor.Object,
            Mock.Of<ILogger<FeedbackWeightedScorer>>());
    }

    [Fact]
    public async Task BlendFeedback_NoTriplets_ReturnsOriginalResults()
    {
        _graphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(Array.Empty<GraphTriplet>());

        var results = CreateRerankedResults(("c1", 0.9), ("c2", 0.8));

        var blended = await _scorer.BlendFeedbackAsync(results, "test query");

        blended.Should().HaveCount(2);
        blended[0].RerankScore.Should().Be(0.9);
        blended[1].RerankScore.Should().Be(0.8);
    }

    [Fact]
    public async Task BlendFeedback_WithHighWeightNode_BoostsScore()
    {
        SetupGraphWithNodeForChunk("c1", "n1", ["c1"]);
        _feedbackStore
            .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight>
            {
                ["n1"] = new() { NodeId = "n1", Weight = 1.0, UpdateCount = 5, LastUpdatedAt = DateTimeOffset.UtcNow }
            });

        var results = CreateRerankedResults(("c1", 0.5));
        var blended = await _scorer.BlendFeedbackAsync(results, "test");

        // adjusted = (1-0.3)*0.5 + 0.3*1.0 = 0.35 + 0.3 = 0.65
        blended[0].RerankScore.Should().BeApproximately(0.65, 0.01);
    }

    [Fact]
    public async Task BlendFeedback_WithLowWeightNode_ReducesScore()
    {
        SetupGraphWithNodeForChunk("c1", "n1", ["c1"]);
        _feedbackStore
            .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight>
            {
                ["n1"] = new() { NodeId = "n1", Weight = 0.0, UpdateCount = 3, LastUpdatedAt = DateTimeOffset.UtcNow }
            });

        var results = CreateRerankedResults(("c1", 0.9));
        var blended = await _scorer.BlendFeedbackAsync(results, "test");

        // adjusted = (1-0.3)*0.9 + 0.3*0.0 = 0.63
        blended[0].RerankScore.Should().BeApproximately(0.63, 0.01);
    }

    [Fact]
    public async Task BlendFeedback_ReSortsByAdjustedScore()
    {
        var sourceNode = new GraphNode { Id = "n1", Name = "Entity", Type = "Thing", ChunkIds = new[] { "c1" } };
        var targetNode = new GraphNode { Id = "n2", Name = "Target", Type = "Thing", ChunkIds = new[] { "c2" } };
        var edge = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "related", ChunkId = "c1"
        };

        _graphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new[]
            {
                new GraphTriplet { Source = sourceNode, Edge = edge, Target = targetNode }
            });

        _feedbackStore
            .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight>
            {
                ["n1"] = new() { NodeId = "n1", Weight = 1.0, UpdateCount = 10, LastUpdatedAt = DateTimeOffset.UtcNow },
                ["n2"] = new() { NodeId = "n2", Weight = 0.0, UpdateCount = 10, LastUpdatedAt = DateTimeOffset.UtcNow }
            });

        SetAlpha(0.5);
        var results = CreateRerankedResults(("c1", 0.4), ("c2", 0.6));
        var blended = await _scorer.BlendFeedbackAsync(results, "test");

        // c1: 0.5*0.4 + 0.5*1.0 = 0.7
        // c2: 0.5*0.6 + 0.5*0.0 = 0.3
        blended[0].RetrievalResult.Chunk.Id.Should().Be("c1");
        blended[0].RerankRank.Should().Be(1);
        blended[1].RetrievalResult.Chunk.Id.Should().Be("c2");
        blended[1].RerankRank.Should().Be(2);
    }

    private void SetAlpha(double alpha)
    {
        _configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { FeedbackAlpha = alpha }
                }
            }
        });
    }

    private void SetupGraphWithNodeForChunk(string chunkId, string nodeId, string[] chunkIds)
    {
        var node = new GraphNode { Id = nodeId, Name = "Entity", Type = "Thing", ChunkIds = chunkIds };
        var edge = new GraphEdge
        {
            Id = $"e-{nodeId}", SourceNodeId = nodeId, TargetNodeId = $"{nodeId}-target",
            Predicate = "related", ChunkId = chunkId
        };
        var target = new GraphNode { Id = $"{nodeId}-target", Name = "Target", Type = "Thing", ChunkIds = chunkIds };

        _graphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new[]
            {
                new GraphTriplet { Source = node, Edge = edge, Target = target }
            });
    }

    private static IReadOnlyList<RerankedResult> CreateRerankedResults(
        params (string ChunkId, double RerankScore)[] items)
    {
        return items.Select((item, i) => new RerankedResult
        {
            RetrievalResult = new RetrievalResult
            {
                Chunk = new DocumentChunk
                {
                    Id = item.ChunkId, DocumentId = "doc1",
                    SectionPath = "/", Content = $"Content for {item.ChunkId}",
                    Tokens = 10,
                    Metadata = new ChunkMetadata
                    {
                        SourceUri = new Uri("https://test.com"),
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                },
                DenseScore = item.RerankScore,
                SparseScore = 0.0,
                FusedScore = item.RerankScore
            },
            RerankScore = item.RerankScore,
            OriginalRank = i + 1,
            RerankRank = i + 1
        }).ToList();
    }
}
