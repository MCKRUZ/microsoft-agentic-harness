using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.PostgreSql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.PostgreSql;

/// <summary>
/// Shared PostgreSQL container + store for the integration tests. Using a single container per test
/// class (via <see cref="IClassFixture{T}"/>) avoids spinning up one container per test method, which
/// speeds the suite up.
/// </summary>
public sealed class PostgreSqlStoreFixture : IAsyncLifetime
{
    // The postgres:16 image boots a temporary init server to run init scripts, shuts it down, then
    // starts the real server. A bare pg_isready (or a log-match on "ready to accept connections") can
    // satisfy against the init server; the test's TCP connection then races the shutdown + restart.
    // Compound wait: "PostgreSQL init process complete; ready for start up." only appears after the
    // init server has already shut down, so when both conditions are satisfied (that message present AND
    // pg_isready passes), the real server is the one answering.
    private readonly IContainer _postgres = new ContainerBuilder()
        .WithImage("postgres:16")
        .WithEnvironment("POSTGRES_PASSWORD", "postgres")
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("PostgreSQL init process complete; ready for start up.")
            .UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
        .Build();

    public PostgreSqlGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var connString =
            $"Host=localhost;Port={_postgres.GetMappedPublicPort(5432)};" +
            "Username=postgres;Password=postgres;Database=postgres";
        var config = new AppConfig();
        config.AI.Rag.GraphRag.ConnectionString = connString;
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);
        Store = new PostgreSqlGraphStore(monitor, NullLogger<PostgreSqlGraphStore>.Instance);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>
/// Integration tests for <see cref="PostgreSqlGraphStore"/> against a real PostgreSQL container.
/// Verifies the self-initializing schema, that owner/tenant/temporal fields round-trip (they were
/// previously absent from INSERT/SELECT), and that the formerly-stubbed
/// <c>GetAllNodesAsync</c>/<c>GetNodesByOwnerAsync</c> now return data. Each test uses unique ids so
/// they remain correct against the shared container.
/// </summary>
/// <remarks>Requires Docker. Gated with <c>[Trait("Category","E2E")]</c> so it can be filtered out.</remarks>
[Trait("Category", "E2E")]
public sealed class PostgreSqlGraphStoreTests : IClassFixture<PostgreSqlStoreFixture>
{
    private readonly PostgreSqlGraphStore _store;

    public PostgreSqlGraphStoreTests(PostgreSqlStoreFixture fixture) => _store = fixture.Store;

    [Fact]
    public async Task AddAndGetNode_CreatesSchemaAndRoundTripsOwnerTenantTemporal()
    {
        // The store creates kg_nodes/kg_edges on first use — no external migration needed.
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = created.AddDays(365);
        await _store.AddNodesAsync([new GraphNode
        {
            Id = "pg-n1", Name = "Acme Corp", Type = "Organization",
            OwnerId = "pg-user-a", TenantId = "pg-tenant-a",
            CreatedAt = created, ExpiresAt = expires
        }]);

        var node = await _store.GetNodeAsync("pg-n1");

        node.Should().NotBeNull();
        node!.OwnerId.Should().Be("pg-user-a");
        node.TenantId.Should().Be("pg-tenant-a");
        node.CreatedAt.Should().BeCloseTo(created, TimeSpan.FromSeconds(1));
        node.ExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetAllNodes_ReturnsInsertedNodesWithTenant()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "pg-all-a", Name = "A", Type = "Entity", TenantId = "t1" },
            new GraphNode { Id = "pg-all-b", Name = "B", Type = "Entity", TenantId = "t2" }
        ]);

        var all = await _store.GetAllNodesAsync();

        all.Select(n => n.Id).Should().Contain(["pg-all-a", "pg-all-b"]);
        all.Single(n => n.Id == "pg-all-a").TenantId.Should().Be("t1");
    }

    [Fact]
    public async Task GetNodesByOwner_ReturnsOnlyMatchingOwner()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "pg-own", Name = "Own", Type = "Entity", OwnerId = "pg-owner-x" },
            new GraphNode { Id = "pg-other", Name = "Other", Type = "Entity", OwnerId = "pg-owner-y" }
        ]);

        var owned = await _store.GetNodesByOwnerAsync("pg-owner-x");

        owned.Select(n => n.Id).Should().BeEquivalentTo(["pg-own"]);
    }

    [Fact]
    public async Task AddAndGetTriplet_RoundTripsEdgeTenant()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "pg-s", Name = "S", Type = "Entity", TenantId = "tenant-a" },
            new GraphNode { Id = "pg-t", Name = "T", Type = "Entity", TenantId = "tenant-a" }
        ]);
        await _store.AddEdgesAsync([new GraphEdge
        {
            Id = "pg-e1", SourceNodeId = "pg-s", TargetNodeId = "pg-t",
            Predicate = "relates_to", ChunkId = "c1", TenantId = "tenant-a"
        }]);

        var triplets = await _store.GetTripletsAsync(["pg-s"]);

        triplets.Should().ContainSingle();
        triplets[0].Edge.TenantId.Should().Be("tenant-a");
        triplets[0].Source.TenantId.Should().Be("tenant-a");
        triplets[0].Target.Id.Should().Be("pg-t");
    }

    [Fact]
    public async Task DeleteNodesAsync_ReturnsActualCountsAndCascadedEdgeIds()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "pg-del-1", Name = "A", Type = "Entity" },
            new GraphNode { Id = "pg-del-2", Name = "B", Type = "Entity" },
            new GraphNode { Id = "pg-del-3", Name = "C", Type = "Entity" }
        ]);
        await _store.AddEdgesAsync([
            new GraphEdge
            {
                Id = "pg-del-e1", SourceNodeId = "pg-del-1", TargetNodeId = "pg-del-2",
                Predicate = "links", ChunkId = "c1"
            },
            new GraphEdge
            {
                Id = "pg-del-e2", SourceNodeId = "pg-del-3", TargetNodeId = "pg-del-1",
                Predicate = "links", ChunkId = "c1"
            },
            new GraphEdge
            {
                Id = "pg-del-e3", SourceNodeId = "pg-del-2", TargetNodeId = "pg-del-3",
                Predicate = "links", ChunkId = "c1"
            }
        ]);

        // One requested id does not exist and must not be counted.
        var result = await _store.DeleteNodesAsync(["pg-del-1", "pg-del-missing"]);

        result.NodesDeleted.Should().Be(1);
        result.DeletedEdgeIds.Should().BeEquivalentTo(["pg-del-e1", "pg-del-e2"]);
        (await _store.GetNodeAsync("pg-del-1")).Should().BeNull();
        (await _store.GetNodeAsync("pg-del-2")).Should().NotBeNull();
        (await _store.GetTripletsAsync(["pg-del-2", "pg-del-3"]))
            .Select(t => t.Edge.Id).Should().Contain("pg-del-e3");
    }

    [Fact]
    public async Task DeleteEdgesByOwnerAsync_RemovesOnlyThatOwnersEdges()
    {
        await _store.AddNodesAsync([
            new GraphNode { Id = "pg-oe-1", Name = "A", Type = "Entity" },
            new GraphNode { Id = "pg-oe-2", Name = "B", Type = "Entity" }
        ]);
        await _store.AddEdgesAsync([
            new GraphEdge
            {
                Id = "pg-oe-owned", SourceNodeId = "pg-oe-1", TargetNodeId = "pg-oe-2",
                Predicate = "links", ChunkId = "c1", OwnerId = "pg-erase-owner"
            },
            new GraphEdge
            {
                Id = "pg-oe-foreign", SourceNodeId = "pg-oe-2", TargetNodeId = "pg-oe-1",
                Predicate = "links", ChunkId = "c1", OwnerId = "pg-other-owner"
            }
        ]);

        var deleted = await _store.DeleteEdgesByOwnerAsync("pg-erase-owner");

        deleted.Should().BeEquivalentTo(["pg-oe-owned"]);
        var remaining = (await _store.GetTripletsAsync(["pg-oe-1", "pg-oe-2"]))
            .Select(t => t.Edge.Id).Distinct().ToList();
        remaining.Should().Contain("pg-oe-foreign");
        remaining.Should().NotContain("pg-oe-owned");
    }
}
