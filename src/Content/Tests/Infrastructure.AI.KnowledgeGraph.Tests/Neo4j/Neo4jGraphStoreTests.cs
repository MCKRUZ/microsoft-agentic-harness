using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Neo4j;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Neo4j;

/// <summary>
/// Shared Neo4j container + store for the integration tests. One container per test class (via
/// <see cref="IClassFixture{T}"/>) rather than one per method — faster and avoids container churn.
/// </summary>
public sealed class Neo4jStoreFixture : IAsyncLifetime
{
    private readonly IContainer _neo4j = new ContainerBuilder()
        .WithImage("neo4j:5.20")
        .WithEnvironment("NEO4J_AUTH", "neo4j/testpassword")
        .WithPortBinding(7687, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Started."))
        .Build();

    public Neo4jGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _neo4j.StartAsync();

        var connString = $"bolt://neo4j:testpassword@localhost:{_neo4j.GetMappedPublicPort(7687)}";
        var config = new AppConfig();
        config.AI.Rag.GraphRag.ConnectionString = connString;
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);
        Store = new Neo4jGraphStore(monitor, NullLogger<Neo4jGraphStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        await _neo4j.DisposeAsync();
    }
}

/// <summary>
/// Integration tests for <see cref="Neo4jGraphStore"/> against a real Neo4j container.
/// Verifies that owner/tenant/temporal fields round-trip through Cypher (they were previously
/// dropped on write) and that the formerly-stubbed <c>GetAllNodesAsync</c>/<c>GetNodesByOwnerAsync</c>
/// now return data — the persistence prerequisites for tenant isolation on this backend. Each test
/// uses unique ids so they remain correct against the shared container.
/// </summary>
/// <remarks>Requires Docker. Gated with <c>[Trait("Category","E2E")]</c> so it can be filtered out.</remarks>
[Trait("Category", "E2E")]
public sealed class Neo4jGraphStoreTests : IClassFixture<Neo4jStoreFixture>
{
    private readonly Neo4jGraphStore _store;

    public Neo4jGraphStoreTests(Neo4jStoreFixture fixture) => _store = fixture.Store;

    [Fact]
    public async Task AddAndGetNode_RoundTripsOwnerTenantAndTemporal()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = created.AddDays(365);
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "neo-n1", Name = "Acme Corp", Type = "Organization",
            OwnerId = "neo-user-a", TenantId = "neo-tenant-a",
            CreatedAt = created, ExpiresAt = expires
        }]);

        var node = await _store.GetNodeAsync("neo-n1");

        node.Should().NotBeNull();
        node!.OwnerId.Should().Be("neo-user-a");
        node.TenantId.Should().Be("neo-tenant-a");
        node.CreatedAt.Should().BeCloseTo(created, TimeSpan.FromSeconds(1));
        node.ExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAllNodes_ReturnsInsertedNodes()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-all-a", Name = "A", Type = "Entity", TenantId = "t1" },
            new GraphNode { Id = "neo-all-b", Name = "B", Type = "Entity", TenantId = "t2" }
        ]);

        var all = await _store.GetAllNodesAsync();

        all.Select(n => n.Id).Should().Contain(["neo-all-a", "neo-all-b"]);
        all.Single(n => n.Id == "neo-all-a").TenantId.Should().Be("t1");
    }

    [Fact]
    public async Task GetNodesByOwner_ReturnsOnlyMatchingOwner()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-own", Name = "Own", Type = "Entity", OwnerId = "neo-owner-x" },
            new GraphNode { Id = "neo-other", Name = "Other", Type = "Entity", OwnerId = "neo-owner-y" }
        ]);

        var owned = await _store.GetNodesByOwnerAsync("neo-owner-x");

        owned.Select(n => n.Id).Should().BeEquivalentTo(["neo-own"]);
    }

    [Fact]
    public async Task AddNode_RewriteWithoutOwnerTenant_PreservesExisting()
    {
        // A later write that omits owner/tenant (e.g. a background/system re-ingest of an entity id
        // that collides with an existing node) must NOT null-clobber the stored owner/tenant, or the
        // node would silently become global and leak across tenants.
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "neo-pres", Name = "P", Type = "Entity",
            OwnerId = "neo-owner-p", TenantId = "neo-tenant-p"
        }]);
        await _store.AddNodesAsync([new GraphNode { Id = "neo-pres", Name = "P2", Type = "Entity" }]);

        var node = await _store.GetNodeAsync("neo-pres");

        node!.OwnerId.Should().Be("neo-owner-p");
        node.TenantId.Should().Be("neo-tenant-p");
    }

    [Fact]
    public async Task AddAndGetTriplet_RoundTripsEdgeTenantAndEndpoints()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-s", Name = "S", Type = "Entity", TenantId = "tenant-a" },
            new GraphNode { Id = "neo-t", Name = "T", Type = "Entity", TenantId = "tenant-a" }
        ]);
        await _store.AddEdgesAsync([new GraphEdge
        {
            Id = "neo-e1", SourceNodeId = "neo-s", TargetNodeId = "neo-t",
            Predicate = "relates_to", ChunkId = "c1", TenantId = "tenant-a"
        }]);

        var triplets = await _store.GetTripletsAsync(["neo-s"]);

        triplets.Should().ContainSingle(t => t.Edge.Id == "neo-e1");
        var triplet = triplets.Single(t => t.Edge.Id == "neo-e1");
        triplet.Edge.SourceNodeId.Should().Be("neo-s");
        triplet.Edge.TargetNodeId.Should().Be("neo-t");
        triplet.Edge.TenantId.Should().Be("tenant-a");
        triplet.Source.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task AddAndGetNode_RoundTripsPropertiesChunksAndProvenance()
    {
        // These fields are stored as JSON strings on the Neo4j node (Properties, Provenance)
        // and as a list (chunk_ids), then re-hydrated on read. Round-tripping them is the
        // provenance/citation contract — a serialization regression here silently corrupts
        // audit lineage and source attribution.
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "rag_ingestion",
            SourceTask = "entity_extraction",
            Timestamp = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero),
            SourceDocumentId = "doc-42",
            ExtractionConfidence = 0.87,
            LastModifiedBy = "neo-user-prov"
        };
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "neo-rich", Name = "Ada Lovelace", Type = "Person",
            Properties = new Dictionary<string, string> { ["role"] = "mathematician", ["era"] = "19c" },
            ChunkIds = ["chunk-a", "chunk-b"],
            Provenance = stamp
        }]);

        var node = await _store.GetNodeAsync("neo-rich");

        node.Should().NotBeNull();
        node!.Properties.Should().Contain("role", "mathematician");
        node.Properties.Should().Contain("era", "19c");
        node.ChunkIds.Should().BeEquivalentTo(["chunk-a", "chunk-b"]);
        node.Provenance.Should().NotBeNull();
        node.Provenance!.SourcePipeline.Should().Be("rag_ingestion");
        node.Provenance.SourceTask.Should().Be("entity_extraction");
        node.Provenance.SourceDocumentId.Should().Be("doc-42");
        node.Provenance.ExtractionConfidence.Should().BeApproximately(0.87, 1e-9);
        node.Provenance.LastModifiedBy.Should().Be("neo-user-prov");
        node.Provenance.Timestamp.Should().BeCloseTo(stamp.Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AddNode_RewriteMergesChunkIds_WithoutDroppingExisting()
    {
        // Re-ingesting an entity from a new chunk must UNION its chunk citations, not overwrite
        // them — otherwise the earlier source reference (c1) is silently lost. A naive
        // "SET chunk_ids = $chunks" would drop c1; the store merges instead.
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "neo-merge", Name = "Graph Theory", Type = "Concept", ChunkIds = ["c1", "c2"]
        }]);
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "neo-merge", Name = "Graph Theory", Type = "Concept", ChunkIds = ["c2", "c3"]
        }]);

        var node = await _store.GetNodeAsync("neo-merge");

        node!.ChunkIds.Should().Contain(["c1", "c2", "c3"]);
    }

    [Fact]
    public async Task AddAndGetTriplet_RoundTripsEdgePropertiesAndProvenance()
    {
        // Same serialization contract as nodes, but for edges via the MapEdge read path.
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "rag_ingestion",
            SourceTask = "relationship_detection",
            Timestamp = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
            ExtractionConfidence = 0.55
        };
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-es", Name = "S", Type = "Entity" },
            new GraphNode { Id = "neo-et", Name = "T", Type = "Entity" }
        ]);
        await _store.AddEdgesAsync([new GraphEdge
        {
            Id = "neo-edge-rich", SourceNodeId = "neo-es", TargetNodeId = "neo-et",
            Predicate = "depends_on", ChunkId = "c-edge",
            Properties = new Dictionary<string, string> { ["weight"] = "0.9" },
            Provenance = stamp
        }]);

        var triplet = (await _store.GetTripletsAsync(["neo-es"])).Single(t => t.Edge.Id == "neo-edge-rich");

        triplet.Edge.Properties.Should().Contain("weight", "0.9");
        triplet.Edge.Provenance.Should().NotBeNull();
        triplet.Edge.Provenance!.SourcePipeline.Should().Be("rag_ingestion");
        triplet.Edge.Provenance.SourceTask.Should().Be("relationship_detection");
        triplet.Edge.Provenance.ExtractionConfidence.Should().BeApproximately(0.55, 1e-9);
        triplet.Edge.Provenance.Timestamp.Should().BeCloseTo(stamp.Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeleteNodesAsync_ReturnsActualCountsAndCascadedEdgeIds()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-del-1", Name = "A", Type = "Entity" },
            new GraphNode { Id = "neo-del-2", Name = "B", Type = "Entity" },
            new GraphNode { Id = "neo-del-3", Name = "C", Type = "Entity" }
        ]);
        await _store.AddEdgesAsync([
            new GraphEdge
            {
                Id = "neo-del-e1", SourceNodeId = "neo-del-1", TargetNodeId = "neo-del-2",
                Predicate = "links", ChunkId = "c1"
            },
            new GraphEdge
            {
                Id = "neo-del-e2", SourceNodeId = "neo-del-3", TargetNodeId = "neo-del-1",
                Predicate = "links", ChunkId = "c1"
            },
            new GraphEdge
            {
                Id = "neo-del-e3", SourceNodeId = "neo-del-2", TargetNodeId = "neo-del-3",
                Predicate = "links", ChunkId = "c1"
            }
        ]);

        // One requested id does not exist and must not be counted.
        var result = await _store.DeleteNodesAsync(["neo-del-1", "neo-del-missing"]);

        result.NodesDeleted.Should().Be(1);
        result.DeletedEdgeIds.Should().BeEquivalentTo(["neo-del-e1", "neo-del-e2"]);
        (await _store.GetNodeAsync("neo-del-1")).Should().BeNull();
        (await _store.GetNodeAsync("neo-del-2")).Should().NotBeNull();
        (await _store.GetTripletsAsync(["neo-del-2", "neo-del-3"]))
            .Select(t => t.Edge.Id).Should().Contain("neo-del-e3");
    }

    [Fact]
    public async Task DeleteEdgesByOwnerAsync_RemovesOnlyThatOwnersEdges()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "neo-oe-1", Name = "A", Type = "Entity" },
            new GraphNode { Id = "neo-oe-2", Name = "B", Type = "Entity" }
        ]);
        await _store.AddEdgesAsync([
            new GraphEdge
            {
                Id = "neo-oe-owned", SourceNodeId = "neo-oe-1", TargetNodeId = "neo-oe-2",
                Predicate = "links", ChunkId = "c1", OwnerId = "neo-erase-owner"
            },
            new GraphEdge
            {
                Id = "neo-oe-foreign", SourceNodeId = "neo-oe-2", TargetNodeId = "neo-oe-1",
                Predicate = "links", ChunkId = "c1", OwnerId = "neo-other-owner"
            }
        ]);

        var deleted = await _store.DeleteEdgesByOwnerAsync("neo-erase-owner");

        deleted.Should().BeEquivalentTo(["neo-oe-owned"]);
        var remaining = (await _store.GetTripletsAsync(["neo-oe-1", "neo-oe-2"]))
            .Select(t => t.Edge.Id).Distinct().ToList();
        remaining.Should().Contain("neo-oe-foreign");
        remaining.Should().NotContain("neo-oe-owned");
    }
}
