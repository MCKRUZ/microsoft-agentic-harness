using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.DependencyInjection;
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

    // ------------------------------------------------------------------
    // Live write path — edges must be erasable WITHOUT hand-stamped OwnerId
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByOwner_DeletesEdgeWrittenThroughLiveDecoratedStorePath()
    {
        // The shipped write path: callers write through ComplianceAwareGraphStore, which is
        // responsible for stamping ownership metadata. If it does not stamp GraphEdge.OwnerId,
        // the owner-edge sweep matches nothing the harness itself writes and erasure is inert.
        var scope = Mock.Of<IKnowledgeScope>(s => s.UserId == "user-1" && s.TenantId == null);
        var services = new ServiceCollection();
        services.AddSingleton(scope);
        var ambient = Mock.Of<IAmbientRequestScope>(a => a.Current == services.BuildServiceProvider());

        var retention = new Mock<IRetentionPolicyProvider>();
        retention.Setup(r => r.GetPolicy(It.IsAny<string>()))
            .Returns(new RetentionPolicy { EntityType = "Entity", RetentionPeriod = TimeSpan.FromDays(365) });

        var decoratedStore = new ComplianceAwareGraphStore(
            _graphStore,
            Mock.Of<IMemoryAuditSink>(),
            ambient,
            retention.Object,
            TimeProvider.System,
            NullLogger<ComplianceAwareGraphStore>.Instance);

        // user-1 writes an edge between two SHARED (unowned) nodes through the live path,
        // without setting OwnerId — exactly what every production call site does.
        await decoratedStore.AddNodesAsync([Node("s1"), Node("s2")]);
        await decoratedStore.AddEdgesAsync([Edge("e-live", "s1", "s2")]);

        var orchestrator = new DefaultErasureOrchestrator(
            decoratedStore,
            _feedbackStore,
            vectorStore: null,
            Mock.Of<IMemoryAuditSink>(),
            TimeProvider.System,
            NullLogger<DefaultErasureOrchestrator>.Instance);

        await orchestrator.EraseByOwnerAsync("user-1");

        var remaining = (await _graphStore.GetTripletsAsync(["s1", "s2"]))
            .Select(t => t.Edge.Id).Distinct().ToList();
        remaining.Should().NotContain("e-live",
            "an edge the erased user wrote through the live decorated store path must be erased");
    }

    // ------------------------------------------------------------------
    // Audit truthfulness — audits must record what was DELETED, not requested
    // ------------------------------------------------------------------

    [Fact]
    public async Task EraseByNodeIds_AuditReportsOnlyNodesActuallyDeleted()
    {
        var auditSink = new Mock<IMemoryAuditSink>();
        MemoryAuditEvent? captured = null;
        auditSink.Setup(a => a.EmitAsync(It.IsAny<MemoryAuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MemoryAuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        var orchestrator = new DefaultErasureOrchestrator(
            _graphStore,
            _feedbackStore,
            vectorStore: null,
            auditSink.Object,
            TimeProvider.System,
            NullLogger<DefaultErasureOrchestrator>.Instance);

        await _graphStore.AddNodesAsync([Node("n1", "user-1")]);

        await orchestrator.EraseByNodeIdsAsync(["n1", "does-not-exist"]);

        captured.Should().NotBeNull();
        captured!.Action.Should().Be(MemoryAuditAction.Erasure);
        captured.AffectedNodeIds.Should().BeEquivalentTo(["n1"],
            "the erasure audit must record the nodes actually deleted, not the requested IDs");
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

    // ------------------------------------------------------------------
    // Gap 4 — retention must purge derived content for EXPIRED nodes even
    // though the compliance-aware store returns null when re-fetching them
    // ------------------------------------------------------------------

    [Fact]
    public async Task RetentionEnforcement_PurgesDerivedContentForExpiredNodes_ThroughComplianceStore()
    {
        // Seed an EXPIRED node carrying derived chunk IDs directly into the inner store, so we
        // control ExpiresAt (bypassing the decorator's retention stamping).
        await _graphStore.AddNodesAsync([new GraphNode
        {
            Id = "expired-1", Name = "Old", Type = "Fact",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            ChunkIds = ["docExpired_chunk_0", "docExpired_raptor_L0_C0"]
        }]);

        // Live decorated store: ComplianceAwareGraphStore.GetNodeAsync returns null for an expired
        // node, so an id-based re-fetch would drop its ChunkIds and skip the vector purge.
        var scope = Mock.Of<IKnowledgeScope>(s => s.UserId == null && s.TenantId == null);
        var services = new ServiceCollection();
        services.AddSingleton(scope);
        var ambient = Mock.Of<IAmbientRequestScope>(a => a.Current == services.BuildServiceProvider());

        var retention = new Mock<IRetentionPolicyProvider>();
        retention.Setup(r => r.GetPolicy(It.IsAny<string>()))
            .Returns(new RetentionPolicy { EntityType = "Fact", RetentionPeriod = TimeSpan.FromDays(365) });

        var decorated = new ComplianceAwareGraphStore(
            _graphStore, Mock.Of<IMemoryAuditSink>(), ambient, retention.Object,
            TimeProvider.System, NullLogger<ComplianceAwareGraphStore>.Instance);

        var vectorStore = new Mock<IVectorStore>();
        var bm25Store = new Mock<IBm25Store>();

        var orchestrator = new DefaultErasureOrchestrator(
            decorated, _feedbackStore, vectorStore.Object, Mock.Of<IMemoryAuditSink>(),
            TimeProvider.System, NullLogger<DefaultErasureOrchestrator>.Instance,
            bm25Store.Object);

        var service = new RetentionEnforcementService(
            decorated, ScopeFactoryFor(orchestrator),
            NullLogger<RetentionEnforcementService>.Instance);

        await service.EnforceRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        vectorStore.Verify(
            v => v.DeleteAsync("docExpired", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the expired node's derived embeddings must be purged — its chunk IDs must survive to the derived-content sweep");
        bm25Store.Verify(
            b => b.DeleteAsync("docExpired", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Builds a real <see cref="IServiceScopeFactory"/> that resolves the given orchestrator from a
    /// fresh scope, mirroring how the singleton retention service obtains its scoped dependency.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(IErasureOrchestrator orchestrator)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => orchestrator);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
