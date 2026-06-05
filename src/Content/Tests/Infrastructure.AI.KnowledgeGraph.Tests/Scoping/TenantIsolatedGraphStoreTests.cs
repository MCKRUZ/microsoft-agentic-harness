using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Scoping;

/// <summary>
/// Tests for <see cref="TenantIsolatedGraphStore"/> — the decorator that filters every graph
/// record against the current caller's <see cref="IKnowledgeScope"/>. Verifies per-record owner
/// isolation (a user never sees another user's owned nodes), shared-corpus visibility (unowned
/// nodes are visible to everyone), and full system access when no request scope is in flight.
/// Drives the <em>real</em> <see cref="KnowledgeScopeValidator"/> so the test proves end-to-end
/// isolation rather than a mocked gate.
/// </summary>
public sealed class TenantIsolatedGraphStoreTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private readonly InMemoryGraphStore _innerStore;
    private readonly KnowledgeScopeValidator _validator;

    public TenantIsolatedGraphStoreTests()
    {
        _innerStore = new InMemoryGraphStore(NullLogger<InMemoryGraphStore>.Instance);

        var config = new AppConfig();
        config.AI.Rag.GraphRag.MultiTenantIsolation = true;
        _validator = new KnowledgeScopeValidator(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));
    }

    [Fact]
    public async Task GetNode_OwnedByCaller_ReturnsNode()
    {
        await SeedNode("n1", owner: UserA);
        var store = StoreFor(UserA);

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNode_OwnedByAnotherUser_ReturnsNull()
    {
        // The core isolation guarantee: User A must never read User B's owned node.
        await SeedNode("n1", owner: UserB);
        var store = StoreFor(UserA);

        (await store.GetNodeAsync("n1")).Should().BeNull();
    }

    [Fact]
    public async Task GetNode_Unowned_ReturnsNode()
    {
        // Unowned (null-owner) nodes are shared corpus — visible to every caller.
        await SeedNode("n1", owner: null);
        var store = StoreFor(UserA);

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNode_NoAmbientScope_ReturnsForeignNode()
    {
        // Background/system work runs outside any request scope and has full access.
        await SeedNode("n1", owner: UserB);
        var store = SystemStore();

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetAllNodes_ReturnsOnlyCallerOwnedAndShared()
    {
        await SeedNode("own", owner: UserA);
        await SeedNode("other", owner: UserB);
        await SeedNode("shared", owner: null);
        var store = StoreFor(UserA);

        var ids = (await store.GetAllNodesAsync()).Select(n => n.Id).ToList();

        ids.Should().BeEquivalentTo(["own", "shared"]);
    }

    [Fact]
    public async Task GetNodeCount_ReflectsOnlyVisibleNodes()
    {
        await SeedNode("own", owner: UserA);
        await SeedNode("other", owner: UserB);
        var store = StoreFor(UserA);

        (await store.GetNodeCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NodeExists_ForeignNode_ReturnsFalse()
    {
        await SeedNode("n1", owner: UserB);
        var store = StoreFor(UserA);

        (await store.NodeExistsAsync("n1")).Should().BeFalse();
    }

    [Fact]
    public async Task AddNodes_ForeignOwner_IsRejected()
    {
        var store = StoreFor(UserA);

        await store.AddNodesAsync([new GraphNode { Id = "n1", Name = "X", Type = "Fact", OwnerId = UserB }]);

        (await _innerStore.GetNodeAsync("n1")).Should().BeNull();
    }

    [Fact]
    public async Task AddNodes_UnownedFreshNode_IsWritten()
    {
        // Fresh nodes arrive unowned and are stamped downstream — they must pass the write filter.
        var store = StoreFor(UserA);

        await store.AddNodesAsync([new GraphNode { Id = "n1", Name = "X", Type = "Fact" }]);

        (await _innerStore.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_ForeignNode_IsBlocked()
    {
        await SeedNode("n1", owner: UserB);
        var store = StoreFor(UserA);

        await store.DeleteNodeAsync("n1");

        (await _innerStore.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNode_OwnedNode_IsDeleted()
    {
        await SeedNode("n1", owner: UserA);
        var store = StoreFor(UserA);

        await store.DeleteNodeAsync("n1");

        (await _innerStore.GetNodeAsync("n1")).Should().BeNull();
    }

    [Fact]
    public async Task GetNeighbors_ForeignSeed_ReturnsEmpty()
    {
        // The seed node is never in the result set, so a foreign seed must be blocked outright —
        // otherwise the caller learns which shared entities another user's private node connects to.
        await SeedNode("seed", owner: UserB);
        await SeedNode("nbr", owner: null);
        await SeedEdge("seed", "nbr");
        var store = StoreFor(UserA);

        (await store.GetNeighborsAsync("seed")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetNeighbors_OwnSeed_ReturnsVisibleNeighbors()
    {
        await SeedNode("seed", owner: UserA);
        await SeedNode("nbr", owner: null);
        await SeedEdge("seed", "nbr");
        var store = StoreFor(UserA);

        (await store.GetNeighborsAsync("seed")).Select(n => n.Id).Should().Contain("nbr");
    }

    private Task SeedNode(string id, string? owner) =>
        _innerStore.AddNodesAsync([new GraphNode { Id = id, Name = id, Type = "Fact", OwnerId = owner }]);

    private Task SeedEdge(string source, string target) =>
        _innerStore.AddEdgesAsync([new GraphEdge
        {
            Id = $"{source}->{target}", SourceNodeId = source, TargetNodeId = target,
            Predicate = "relates_to", ChunkId = "c1"
        }]);

    private TenantIsolatedGraphStore StoreFor(string userId)
    {
        var scope = Mock.Of<IKnowledgeScope>(s => s.UserId == userId);
        var services = new ServiceCollection();
        services.AddSingleton(scope);
        var ambient = Mock.Of<IAmbientRequestScope>(a => a.Current == services.BuildServiceProvider());
        return new TenantIsolatedGraphStore(
            _innerStore, ambient, _validator, NullLogger<TenantIsolatedGraphStore>.Instance);
    }

    private TenantIsolatedGraphStore SystemStore() =>
        new(_innerStore, Mock.Of<IAmbientRequestScope>(), _validator,
            NullLogger<TenantIsolatedGraphStore>.Instance);
}
