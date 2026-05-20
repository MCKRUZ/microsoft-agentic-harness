using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Tests for <see cref="FeedbackWeightedScorer"/> enhancement — feedback persistence
/// to the graph backend after blending.
/// </summary>
public sealed class FeedbackWeightedScorerPersistenceTests
{
    [Fact]
    public async Task BlendFeedbackAsync_PersistsFeedbackToGraph()
    {
        // Arrange
        var mockFeedbackStore = new Mock<IFeedbackStore>();
        var mockGraphStore = new Mock<IGraphDatabaseBackend>();

        var nodeWeight = new NodeFeedbackWeight
        {
            NodeId = "n1",
            Weight = 0.8,
            UpdateCount = 5,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
        mockFeedbackStore
            .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight> { ["n1"] = nodeWeight });

        var triplet = new GraphTriplet
        {
            Source = RagTestData.CreateGraphNode("n1", "Azure", "Tech", chunkIds: ["chunk-1"]),
            Edge = RagTestData.CreateGraphEdge("e1", "n1", "n2", "uses"),
            Target = RagTestData.CreateGraphNode("n2", "OpenAI", "Org")
        };
        mockGraphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphTriplet> { triplet });

        var configMonitor = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.GraphRag.FeedbackAlpha = 0.3;
            c.AI.Rag.GraphRag.FeedbackEnabled = true;
        });

        var sut = new FeedbackWeightedScorer(
            mockFeedbackStore.Object,
            mockGraphStore.Object,
            configMonitor,
            NullLogger<FeedbackWeightedScorer>.Instance);

        var reranked = new List<RerankedResult> { RagTestData.CreateRerankedResult("chunk-1") };

        // Act
        await sut.BlendFeedbackAsync(reranked, "test query");

        // Assert
        mockGraphStore.Verify(
            g => g.UpdateNodeWeightAsync("n1", It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "should persist the blended feedback weight back to the graph");
    }

    [Fact]
    public async Task BlendFeedbackAsync_NoGraphNodes_SkipsPersistence()
    {
        // Arrange
        var mockFeedbackStore = new Mock<IFeedbackStore>();
        var mockGraphStore = new Mock<IGraphDatabaseBackend>();

        mockGraphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GraphTriplet>());

        var configMonitor = RagTestData.CreateConfigMonitor(c =>
            c.AI.Rag.GraphRag.FeedbackAlpha = 0.3);

        var sut = new FeedbackWeightedScorer(
            mockFeedbackStore.Object,
            mockGraphStore.Object,
            configMonitor,
            NullLogger<FeedbackWeightedScorer>.Instance);

        var reranked = new List<RerankedResult> { RagTestData.CreateRerankedResult() };

        // Act
        await sut.BlendFeedbackAsync(reranked, "test query");

        // Assert
        mockGraphStore.Verify(
            g => g.UpdateNodeWeightAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "should not persist when no graph nodes match");
    }

    [Fact]
    public async Task BlendFeedbackAsync_UpdatesExistingWeights()
    {
        // Arrange
        var mockFeedbackStore = new Mock<IFeedbackStore>();
        var mockGraphStore = new Mock<IGraphDatabaseBackend>();

        var nodeWeight = new NodeFeedbackWeight
        {
            NodeId = "n1",
            Weight = 0.5,
            UpdateCount = 10,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
        mockFeedbackStore
            .Setup(f => f.GetNodeWeightsBatchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, NodeFeedbackWeight> { ["n1"] = nodeWeight });

        var triplet = new GraphTriplet
        {
            Source = RagTestData.CreateGraphNode("n1", "Azure", "Tech", chunkIds: ["chunk-1"]),
            Edge = RagTestData.CreateGraphEdge("e1", "n1", "n2", "uses"),
            Target = RagTestData.CreateGraphNode("n2", "OpenAI", "Org")
        };
        mockGraphStore
            .Setup(g => g.GetTripletsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphTriplet> { triplet });

        var configMonitor = RagTestData.CreateConfigMonitor(c =>
        {
            c.AI.Rag.GraphRag.FeedbackAlpha = 0.3;
            c.AI.Rag.GraphRag.FeedbackEnabled = true;
        });

        var sut = new FeedbackWeightedScorer(
            mockFeedbackStore.Object,
            mockGraphStore.Object,
            configMonitor,
            NullLogger<FeedbackWeightedScorer>.Instance);

        var reranked = new List<RerankedResult>
        {
            RagTestData.CreateRerankedResult("chunk-1", rerankScore: 0.9)
        };

        // Act
        await sut.BlendFeedbackAsync(reranked, "Azure query");

        // Assert — the persisted weight should be the blended score
        mockGraphStore.Verify(
            g => g.UpdateNodeWeightAsync("n1",
                It.Is<double>(w => w > 0.0 && w <= 1.0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
