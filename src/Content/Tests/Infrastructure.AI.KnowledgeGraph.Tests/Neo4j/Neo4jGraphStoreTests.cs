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
/// Integration tests for <see cref="Neo4jGraphStore"/> against a real Neo4j container.
/// Verifies that owner/tenant/temporal fields round-trip through Cypher (they were previously
/// dropped on write) and that the formerly-stubbed <c>GetAllNodesAsync</c>/<c>GetNodesByOwnerAsync</c>
/// now return data — the persistence prerequisites for tenant isolation on this backend.
/// </summary>
/// <remarks>Requires Docker. Gated with <c>[Trait("Category","E2E")]</c> so it can be filtered out.</remarks>
[Trait("Category", "E2E")]
public sealed class Neo4jGraphStoreTests : IAsyncLifetime
{
    private readonly IContainer _neo4j = new ContainerBuilder()
        .WithImage("neo4j:5.20")
        .WithEnvironment("NEO4J_AUTH", "neo4j/testpassword")
        .WithPortBinding(7687, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Started."))
        .Build();

    private Neo4jGraphStore _store = null!;

    public async Task InitializeAsync()
    {
        await _neo4j.StartAsync();

        var connString = $"bolt://neo4j:testpassword@localhost:{_neo4j.GetMappedPublicPort(7687)}";
        var config = new AppConfig();
        config.AI.Rag.GraphRag.ConnectionString = connString;
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);
        _store = new Neo4jGraphStore(monitor, NullLogger<Neo4jGraphStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _neo4j.DisposeAsync();
    }

    [Fact]
    public async Task AddAndGetNode_RoundTripsOwnerTenantAndTemporal()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = created.AddDays(365);
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "n1", Name = "Acme Corp", Type = "Organization",
            OwnerId = "user-a", TenantId = "tenant-a",
            CreatedAt = created, ExpiresAt = expires
        }]);

        var node = await _store.GetNodeAsync("n1");

        node.Should().NotBeNull();
        node!.OwnerId.Should().Be("user-a");
        node.TenantId.Should().Be("tenant-a");
        node.CreatedAt.Should().BeCloseTo(created, TimeSpan.FromSeconds(1));
        node.ExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAllNodes_ReturnsInsertedNodes()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "a", Name = "A", Type = "Entity", TenantId = "t1" },
            new GraphNode { Id = "b", Name = "B", Type = "Entity", TenantId = "t2" }
        ]);

        var all = await _store.GetAllNodesAsync();

        all.Select(n => n.Id).Should().Contain(["a", "b"]);
        all.Single(n => n.Id == "a").TenantId.Should().Be("t1");
    }

    [Fact]
    public async Task GetNodesByOwner_ReturnsOnlyMatchingOwner()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "own", Name = "Own", Type = "Entity", OwnerId = "user-x" },
            new GraphNode { Id = "other", Name = "Other", Type = "Entity", OwnerId = "user-y" }
        ]);

        var owned = await _store.GetNodesByOwnerAsync("user-x");

        owned.Select(n => n.Id).Should().BeEquivalentTo(["own"]);
    }

    [Fact]
    public async Task AddAndGetTriplet_RoundTripsEdgeTenantAndEndpoints()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "s", Name = "S", Type = "Entity", TenantId = "tenant-a" },
            new GraphNode { Id = "t", Name = "T", Type = "Entity", TenantId = "tenant-a" }
        ]);
        await _store.AddEdgesAsync([new GraphEdge
        {
            Id = "e1", SourceNodeId = "s", TargetNodeId = "t",
            Predicate = "relates_to", ChunkId = "c1", TenantId = "tenant-a"
        }]);

        var triplets = await _store.GetTripletsAsync(["s"]);

        triplets.Should().ContainSingle();
        var edge = triplets[0].Edge;
        edge.SourceNodeId.Should().Be("s");
        edge.TargetNodeId.Should().Be("t");
        edge.TenantId.Should().Be("tenant-a");
        triplets[0].Source.TenantId.Should().Be("tenant-a");
    }
}
