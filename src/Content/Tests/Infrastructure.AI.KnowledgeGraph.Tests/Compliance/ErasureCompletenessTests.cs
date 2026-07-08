using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// End-to-end erasure completeness tests (audit item D3) running the orchestrator against
/// the REAL in-memory graph and feedback stores — not mocks — so they prove what actually
/// remains in the stores after a right-to-erasure request:
/// <list type="number">
///   <item>Owner-scoped edges between surviving nodes are deleted, not just node-cascade edges.</item>
///   <item><see cref="ErasureReceipt"/> counts reflect what the delete operations actually removed.</item>
///   <item>Feedback weights referencing erased nodes AND edges are purged.</item>
/// </list>
/// </summary>
public sealed class ErasureCompletenessTests
{
    private readonly InMemoryGraphStore _graphStore;
    private readonly GraphFeedbackStore _feedbackStore;
    private readonly DefaultErasureOrchestrator _orchestrator;

    public ErasureCompletenessTests()
    {
        _graphStore = new InMemoryGraphStore(NullLogger<InMemoryGraphStore>.Instance);
        _feedbackStore = new GraphFeedbackStore(
            NullLogger<GraphFeedbackStore>.Instance, TimeProvider.System);

        _orchestrator = new DefaultErasureOrchestrator(
            _graphStore,
            _feedbackStore,
            vectorStore: null,
            Mock.Of<IMemoryAuditSink>(),
            TimeProvider.System,
            NullLogger<DefaultErasureOrchestrator>.Instance);
    }

    private static GraphNode Node(string id, string? ownerId = null) => new()
    {
        Id = id, Name = id, Type = "Entity", OwnerId = ownerId
    };

    private static GraphEdge Edge(string id, string source, string target, string? ownerId = null) => new()
    {
        Id = id, SourceNodeId = source, TargetNodeId = target,
        Predicate = "relates_to", ChunkId = "chunk-1", OwnerId = ownerId
    };

    // ------------------------------------------------------------------
    // Finding 1 — owner-scoped edge deletion
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_DeletesOwnerScopedEdgesBetweenSurvivingNodes()
    {
        // Shared nodes owned by nobody, plus one node owned by the erased subject.
        await _graphStore.AddNodesAsync([Node("s1"), Node("s2"), Node("u1-node", "user-1")]);
        await _graphStore.AddEdgesAsync([
            Edge("e-owned-by-u1", "s1", "s2", "user-1"),   // owner's edge between SURVIVING nodes
            Edge("e-cascade", "u1-node", "s2"),            // removed by node cascade
            Edge("e-foreign", "s2", "s1", "user-2")        // another owner's edge — must survive
        ]);

        await _orchestrator.EraseByOwnerAsync("user-1");

        var triplets = await _graphStore.GetTripletsAsync(["s1", "s2"]);
        var remainingEdgeIds = triplets.Select(t => t.Edge.Id).Distinct().ToList();

        remainingEdgeIds.Should().NotContain("e-owned-by-u1",
            "erasing an owner must delete edges they own even when both endpoints survive");
        remainingEdgeIds.Should().NotContain("e-cascade");
        remainingEdgeIds.Should().Contain("e-foreign",
            "erasure must not delete another owner's edges");
    }

    // ------------------------------------------------------------------
    // Finding 2 — receipt counts must reflect actual deletions
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_ReceiptReportsActualEdgesDeleted()
    {
        await _graphStore.AddNodesAsync([Node("s1"), Node("s2"), Node("u1-node", "user-1")]);
        await _graphStore.AddEdgesAsync([
            Edge("e-owned-by-u1", "s1", "s2", "user-1"),
            Edge("e-cascade", "u1-node", "s2"),
            Edge("e-foreign", "s2", "s1", "user-2")
        ]);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        receipt.NodesDeleted.Should().Be(1);
        receipt.EdgesDeleted.Should().Be(2,
            "the cascade edge and the owner-scoped edge were actually removed; the foreign edge was not");
    }

    [Fact]
    public async Task EraseByOwner_ReceiptReportsActualFeedbackWeightsDeleted()
    {
        // Two owned nodes, but feedback was only ever recorded against one of them.
        await _graphStore.AddNodesAsync([Node("n1", "user-1"), Node("n2", "user-1")]);
        await _feedbackStore.ApplyNodeFeedbackAsync("n1", feedbackScore: 5.0, alpha: 0.3);

        var receipt = await _orchestrator.EraseByOwnerAsync("user-1");

        receipt.FeedbackWeightsDeleted.Should().Be(1,
            "only one feedback weight actually existed, so only one was deleted");
    }

    [Fact]
    public async Task EraseByNodeIds_ReceiptCountsOnlyNodesActuallyDeleted()
    {
        await _graphStore.AddNodesAsync([Node("n1", "user-1")]);

        var receipt = await _orchestrator.EraseByNodeIdsAsync(["n1", "does-not-exist"]);

        receipt.NodesDeleted.Should().Be(1,
            "the receipt must report nodes the store actually removed, not the requested count");
    }

    // ------------------------------------------------------------------
    // Finding 3 — feedback purge must cover edges
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_PurgesFeedbackWeightsForErasedEdges()
    {
        await _graphStore.AddNodesAsync([Node("s1"), Node("u1-node", "user-1")]);
        await _graphStore.AddEdgesAsync([Edge("e-cascade", "u1-node", "s1")]);
        await _feedbackStore.ApplyEdgeFeedbackAsync("e-cascade", feedbackScore: 5.0, alpha: 0.3);

        await _orchestrator.EraseByOwnerAsync("user-1");

        var weight = await _feedbackStore.GetEdgeWeightAsync("e-cascade");
        weight.UpdateCount.Should().Be(0,
            "feedback recorded against an erased edge is a dangling reference that leaks signal about erased data");
    }

    [Fact]
    public async Task EraseByOwner_PurgesFeedbackWeightsForOwnerScopedEdges()
    {
        await _graphStore.AddNodesAsync([Node("s1"), Node("s2"), Node("u1-node", "user-1")]);
        await _graphStore.AddEdgesAsync([Edge("e-owned-by-u1", "s1", "s2", "user-1")]);
        await _feedbackStore.ApplyEdgeFeedbackAsync("e-owned-by-u1", feedbackScore: 4.0, alpha: 0.3);

        await _orchestrator.EraseByOwnerAsync("user-1");

        var weight = await _feedbackStore.GetEdgeWeightAsync("e-owned-by-u1");
        weight.UpdateCount.Should().Be(0);
    }
}
