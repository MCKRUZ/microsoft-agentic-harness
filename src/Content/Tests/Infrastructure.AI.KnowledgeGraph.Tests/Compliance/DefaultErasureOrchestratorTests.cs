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

    // ------------------------------------------------------------------
    // Gap 3 — vector deletion must use the documentId, not the chunkId
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_DeletesVectorsByDocumentId_NotChunkId()
    {
        // Chunk IDs are "{documentId}_chunk_{i}" / "{documentId}_raptor_*"; IVectorStore.DeleteAsync
        // deletes by documentId. Passing a chunkId matches nothing, so the embeddings survive.
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GraphNode
                {
                    Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1",
                    ChunkIds = ["docA_chunk_0", "docA_chunk_1", "docA_raptor_L0_C0"]
                }
            ]);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        // All three chunk IDs derive to the single document "docA" — deleted once.
        _vectorStore.Verify(v => v.DeleteAsync("docA", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _vectorStore.Verify(v => v.DeleteAsync(
            It.Is<string>(id => id.Contains("_chunk_") || id.Contains("_raptor_")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        receipt.VectorEmbeddingsDeleted.Should().Be(1, "one distinct document's embeddings were submitted");
    }

    // ------------------------------------------------------------------
    // Gap 2 — BM25 (and RAPTOR) content must be purged by documentId
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_DeletesBm25ByDocumentId()
    {
        var bm25 = new Mock<IBm25Store>();
        var orchestrator = OrchestratorWith(bm25: bm25.Object);

        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GraphNode
                {
                    Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1",
                    ChunkIds = ["docA_chunk_0", "docA_raptor_L1_C2"]
                }
            ]);

        var receipt = await orchestrator.EraseByOwnerAsync("user-1");

        bm25.Verify(b => b.DeleteAsync("docA", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        receipt.Bm25DocumentsDeleted.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Gap 1 — cross-session memory must be purged on owner erasure
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_PurgesCrossSessionMemory()
    {
        var memory = new Mock<ICrossSessionMemoryStore>();
        memory.Setup(m => m.PurgeByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var orchestrator = OrchestratorWith(crossSession: memory.Object);

        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GraphNode { Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1" }]);

        var receipt = await orchestrator.EraseByOwnerAsync("user-1");

        memory.Verify(m => m.PurgeByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        receipt.CrossSessionMemoriesDeleted.Should().Be(3);
    }

    [Fact]
    public async Task EraseByNodeIds_DoesNotPurgeCrossSessionMemory()
    {
        var memory = new Mock<ICrossSessionMemoryStore>();
        var orchestrator = OrchestratorWith(crossSession: memory.Object);
        _graphStore.Setup(g => g.GetNodeAsync("n1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNode { Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1" });

        await orchestrator.EraseByNodeIdsAsync(["n1"]);

        // Memory is owner-scoped; a node-scoped erasure must not touch it.
        memory.Verify(m => m.PurgeByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Gap 5 — degraded scope must NOT report a clean-success receipt
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_ResolvedOwner_ReceiptIsFull()
    {
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GraphNode { Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1" }]);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        receipt.CompletenessReason.Should().BeNull();
    }

    [Fact]
    public async Task EraseByOwner_DifferentlyCasedOwner_CanonicalizesEverySweep_ReportsFull()
    {
        var memory = new Mock<ICrossSessionMemoryStore>();
        memory.Setup(m => m.PurgeByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var orchestrator = OrchestratorWith(crossSession: memory.Object);

        // The subject's data is stored under the canonical (lowercase) owner; the erasure request
        // arrives with different casing. Every owner-scoped sweep must canonicalize, or graph
        // nodes/edges survive while the receipt dishonestly reports Full.
        _graphStore.Setup(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GraphNode { Id = "n1", Name = "T", Type = "Fact", OwnerId = "user-1" }]);

        var receipt = await orchestrator.EraseByOwnerAsync("User-1");

        _graphStore.Verify(g => g.GetNodesByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        _graphStore.Verify(g => g.GetNodesByOwnerAsync("User-1", It.IsAny<CancellationToken>()), Times.Never);
        _graphStore.Verify(g => g.DeleteEdgesByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        memory.Verify(m => m.PurgeByOwnerAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        receipt.NodesDeleted.Should().Be(1, "the node stored under the canonical owner must be erased");
        receipt.CrossSessionMemoriesDeleted.Should().Be(1);
        receipt.ScopeId.Should().Be("user-1", "the receipt scope is the canonical owner");
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task EraseByOwner_UnresolvableOwner_ReceiptIsPartial()
    {
        var memory = new Mock<ICrossSessionMemoryStore>();
        var orchestrator = OrchestratorWith(crossSession: memory.Object);

        // Whitespace owner canonicalizes to null: EVERY owner-scoped sweep is skipped uniformly.
        var receipt = await orchestrator.EraseByOwnerAsync("   ");

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial,
            "an owner erasure that could not resolve its owner must not present as a clean success");
        receipt.CompletenessReason.Should().NotBeNullOrEmpty();
        receipt.CrossSessionMemoriesDeleted.Should().Be(0);
        // No owner-scoped store call may run with a degraded (null) owner — proving the reason is honest.
        _graphStore.Verify(g => g.GetNodesByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _graphStore.Verify(g => g.DeleteEdgesByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        memory.Verify(m => m.PurgeByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private DefaultErasureOrchestrator OrchestratorWith(
        IBm25Store? bm25 = null,
        ICrossSessionMemoryStore? crossSession = null) =>
        new(
            _graphStore.Object,
            _feedbackStore.Object,
            _vectorStore.Object,
            _auditSink.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DefaultErasureOrchestrator>>(),
            bm25,
            crossSession);
}
