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
/// both speeds the suite up and sidesteps the postgres image's init-then-restart readiness race.
/// </summary>
public sealed class PostgreSqlStoreFixture : IAsyncLifetime
{
    private readonly IContainer _postgres = new ContainerBuilder()
        .WithImage("postgres:16")
        .WithEnvironment("POSTGRES_PASSWORD", "postgres")
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
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
}
