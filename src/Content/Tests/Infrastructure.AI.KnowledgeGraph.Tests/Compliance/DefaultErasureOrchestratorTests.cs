using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

public sealed class DefaultErasureOrchestratorTests
{
    private readonly Mock<IKnowledgeGraphStore> _graphStore;
    private readonly Mock<IFeedbackStore> _feedbackStore;
    private readonly Mock<IVectorStore> _vectorStore;
    private readonly Mock<IMemoryAuditSink> _auditSink;
    private readonly DefaultErasureOrchestrator _orchestrator;

    public DefaultErasureOrchestratorTests()
    {
        _graphStore = new Mock<IKnowledgeGraphStore>();
        _feedbackStore = new Mock<IFeedbackStore>();
        _vectorStore = new Mock<IVectorStore>();
        _auditSink = new Mock<IMemoryAuditSink>();

        _orchestrator = new DefaultErasureOrchestrator(
            _graphStore.Object,
            _feedbackStore.Object,
            _vectorStore.Object,
            _auditSink.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DefaultErasureOrchestrator>>());
    }

    [Fact]
    public async Task EraseByOwner_CascadesAcrossAllStores()
    {
        var nodes = new List<GraphNode>
        {
            new() { Id = "n1", Name = "Test1", Type = "Fact", OwnerId = "user-1", ChunkIds = ["c1"] },
            new() { Id = "n2", Name = "Test2", Type = "Fact", OwnerId = "user-1", ChunkIds = ["c2", "c3"] }
        };
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        receipt.ScopeId.Should().Be("user-1");
        receipt.NodesDeleted.Should().Be(2);

        _graphStore.Verify(g => g.DeleteNodeAsync("n1", It.IsAny<CancellationToken>()), Times.Once);
        _graphStore.Verify(g => g.DeleteNodeAsync("n2", It.IsAny<CancellationToken>()), Times.Once);
        _feedbackStore.Verify(f => f.DeleteWeightsByNodeIdsAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        _auditSink.Verify(a => a.EmitAsync(
            It.Is<MemoryAuditEvent>(e => e.Action == MemoryAuditAction.Erasure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EraseByOwner_NoNodes_ReturnsZeroCounts()
    {
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("nobody", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphNode>());

        var receipt = await _orchestrator.EraseByOwnerAsync("nobody");

        receipt.NodesDeleted.Should().Be(0);
        receipt.FeedbackWeightsDeleted.Should().Be(0);
    }

    [Fact]
    public async Task EraseByNodeIds_DeletesSpecificNodes()
    {
        var nodeIds = new List<string> { "n1", "n2" };
        _graphStore.Setup(g => g.GetNodeAsync("n1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNode { Id = "n1", Name = "T1", Type = "Fact", ChunkIds = ["c1"] });
        _graphStore.Setup(g => g.GetNodeAsync("n2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNode { Id = "n2", Name = "T2", Type = "Fact", ChunkIds = [] });

        var receipt = await _orchestrator.EraseByNodeIdsAsync(nodeIds);

        receipt.NodesDeleted.Should().Be(2);
        _graphStore.Verify(g => g.DeleteNodeAsync("n1", It.IsAny<CancellationToken>()), Times.Once);
        _graphStore.Verify(g => g.DeleteNodeAsync("n2", It.IsAny<CancellationToken>()), Times.Once);
    }
}
