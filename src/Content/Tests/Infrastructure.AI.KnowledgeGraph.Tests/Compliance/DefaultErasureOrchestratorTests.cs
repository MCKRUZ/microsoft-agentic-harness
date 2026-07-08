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

        // Default: the store deletes everything it is asked to and cascades no edges.
        _graphStore.Setup(g => g.DeleteNodesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) =>
                new NodeDeletionResult { DeletedNodeIds = ids.ToList(), DeletedEdgeIds = [] });
        _graphStore.Setup(g => g.DeleteEdgesByOwnerAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _feedbackStore.Setup(f => f.DeleteWeightsByNodeIdsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) => ids.Count);
        _feedbackStore.Setup(f => f.DeleteWeightsByEdgeIdsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) => ids.Count);

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

        _graphStore.Verify(g => g.DeleteNodesAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids.Contains("n1") && ids.Contains("n2")),
            It.IsAny<CancellationToken>()), Times.Once);
        _graphStore.Verify(g => g.DeleteEdgesByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        _feedbackStore.Verify(f => f.DeleteWeightsByNodeIdsAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        _auditSink.Verify(a => a.EmitAsync(
            It.Is<MemoryAuditEvent>(e => e.Action == MemoryAuditAction.Erasure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EraseByOwner_ReceiptUsesCountsReturnedByStores_NotRequestedCounts()
    {
        var nodes = new List<GraphNode>
        {
            new() { Id = "n1", Name = "Test1", Type = "Fact", OwnerId = "user-1" },
            new() { Id = "n2", Name = "Test2", Type = "Fact", OwnerId = "user-1" }
        };
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        // The store reports fewer deletions than requested (e.g. a concurrent delete won).
        _graphStore.Setup(g => g.DeleteNodesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeDeletionResult { DeletedNodeIds = ["n1"], DeletedEdgeIds = ["e-cascade"] });
        _graphStore.Setup(g => g.DeleteEdgesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["e-owned"]);
        _feedbackStore.Setup(f => f.DeleteWeightsByNodeIdsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _feedbackStore.Setup(f => f.DeleteWeightsByEdgeIdsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        receipt.NodesDeleted.Should().Be(1, "the receipt must report the store's actual deletion count");
        receipt.EdgesDeleted.Should().Be(2, "cascade edge + owner-scoped edge were actually removed");
        receipt.FeedbackWeightsDeleted.Should().Be(3, "1 node weight + 2 edge weights were actually removed");
    }

    [Fact]
    public async Task EraseByOwner_PurgesEdgeFeedbackForAllDeletedEdges()
    {
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GraphNode { Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1" }]);
        _graphStore.Setup(g => g.DeleteNodesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeDeletionResult { DeletedNodeIds = ["n1"], DeletedEdgeIds = ["e1", "e2"] });
        _graphStore.Setup(g => g.DeleteEdgesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["e2", "e3"]);

        await _orchestrator.EraseByOwnerAsync("user-1");

        // Union of cascade + owner edges, deduplicated.
        _feedbackStore.Verify(f => f.DeleteWeightsByEdgeIdsAsync(
            It.Is<IReadOnlyList<string>>(ids =>
                ids.Count == 3 && ids.Contains("e1") && ids.Contains("e2") && ids.Contains("e3")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EraseByOwner_AuditRecordsActualDeletedIds_NotRequested()
    {
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GraphNode { Id = "n1", Name = "T1", Type = "Fact", OwnerId = "user-1" },
                new GraphNode { Id = "n2", Name = "T2", Type = "Fact", OwnerId = "user-1" }
            ]);
        // The store only actually removed n1 (n2 raced away).
        _graphStore.Setup(g => g.DeleteNodesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeDeletionResult { DeletedNodeIds = ["n1"], DeletedEdgeIds = ["e1"] });

        await _orchestrator.EraseByOwnerAsync("user-1");

        _auditSink.Verify(a => a.EmitAsync(
            It.Is<MemoryAuditEvent>(e =>
                e.Action == MemoryAuditAction.Erasure &&
                e.AffectedNodeIds!.Count == 1 && e.AffectedNodeIds.Contains("n1") &&
                e.AffectedEdgeIds!.Contains("e1")),
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
        _graphStore.Verify(g => g.DeleteNodesAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids.Contains("n1") && ids.Contains("n2")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EraseByNodeIds_DoesNotSweepOwnerEdges()
    {
        _graphStore.Setup(g => g.GetNodeAsync("n1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNode { Id = "n1", Name = "T1", Type = "Fact", OwnerId = "user-1" });

        await _orchestrator.EraseByNodeIdsAsync(["n1"]);

        // Node-scoped erasure targets nodes, not the owner's whole edge set.
        _graphStore.Verify(g => g.DeleteEdgesByOwnerAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
