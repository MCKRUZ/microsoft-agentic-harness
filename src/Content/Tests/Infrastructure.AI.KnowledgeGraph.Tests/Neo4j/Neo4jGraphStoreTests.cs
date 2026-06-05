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
}
