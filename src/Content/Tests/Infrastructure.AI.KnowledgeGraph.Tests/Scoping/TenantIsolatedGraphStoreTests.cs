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
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

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

    // --- Tenant-level isolation ---

    [Fact]
    public async Task GetNode_DifferentTenant_SharedCorpus_ReturnsNull()
    {
        // A tenant's shared corpus (null owner) must be invisible to other tenants.
        await SeedTenantNode("n1", tenant: TenantB, owner: null);
        var store = StoreForTenant(UserA, TenantA);

        (await store.GetNodeAsync("n1")).Should().BeNull();
    }

    [Fact]
    public async Task GetNode_SameTenant_SharedCorpus_ReturnsNode()
    {
        await SeedTenantNode("n1", tenant: TenantA, owner: null);
        var store = StoreForTenant(UserA, TenantA);

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNode_SameTenant_OtherUsersMemory_ReturnsNull()
    {
        // Within one tenant, a user still cannot read another user's owned memory.
        await SeedTenantNode("n1", tenant: TenantA, owner: UserB);
        var store = StoreForTenant(UserA, TenantA);

        (await store.GetNodeAsync("n1")).Should().BeNull();
    }

    [Fact]
    public async Task GetNode_SameTenant_OwnMemory_ReturnsNode()
    {
        await SeedTenantNode("n1", tenant: TenantA, owner: UserA);
        var store = StoreForTenant(UserA, TenantA);

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNode_GlobalNullTenant_VisibleAcrossTenants()
    {
        // Global (null-tenant) reference data is visible to every tenant.
        await SeedTenantNode("n1", tenant: null, owner: null);
        var store = StoreForTenant(UserA, TenantA);

        (await store.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetAllNodes_FiltersByTenantAndOwner()
    {
        await SeedTenantNode("mine", tenant: TenantA, owner: UserA);
        await SeedTenantNode("tenantShared", tenant: TenantA, owner: null);
        await SeedTenantNode("otherTenant", tenant: TenantB, owner: null);
        await SeedTenantNode("otherUserSameTenant", tenant: TenantA, owner: UserB);
        await SeedTenantNode("global", tenant: null, owner: null);
        var store = StoreForTenant(UserA, TenantA);

        var ids = (await store.GetAllNodesAsync()).Select(n => n.Id).ToList();

        ids.Should().BeEquivalentTo(["mine", "tenantShared", "global"]);
    }

    // --- Batch deletion (right-to-erasure primitives) ---

    [Fact]
    public async Task DeleteNodes_ScopedCaller_SkipsForeignNodesButDeletesOwn()
    {
        await SeedNode("mine", owner: UserA);
        await SeedNode("theirs", owner: UserB);
        var store = StoreFor(UserA);

        var result = await store.DeleteNodesAsync(["mine", "theirs"]);

        result.NodesDeleted.Should().Be(1);
        (await _innerStore.GetNodeAsync("mine")).Should().BeNull();
        (await _innerStore.GetNodeAsync("theirs")).Should().NotBeNull(
            "a scoped caller must never cascade-delete another user's node");
    }

    [Fact]
    public async Task DeleteNodes_NoAmbientScope_DeletesEverythingRequested()
    {
        // Right-to-erasure runs system-scoped and must reach foreign-owned nodes.
        await SeedNode("theirs", owner: UserB);
        var store = SystemStore();

        var result = await store.DeleteNodesAsync(["theirs"]);

        result.NodesDeleted.Should().Be(1);
        (await _innerStore.GetNodeAsync("theirs")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteEdgesByOwner_ScopedCaller_ForeignOwner_IsBlocked()
    {
        await SeedNode("s", owner: null);
        await SeedNode("t", owner: null);
        await _innerStore.AddEdgesAsync([new GraphEdge
        {
            Id = "e1", SourceNodeId = "s", TargetNodeId = "t",
            Predicate = "relates_to", ChunkId = "c1", OwnerId = UserB
        }]);
        var store = StoreFor(UserA);

        var deleted = await store.DeleteEdgesByOwnerAsync(UserB);

        deleted.Should().BeEmpty("a scoped caller cannot erase another owner's edges");
        (await _innerStore.GetEdgeCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteEdgesByOwner_NoAmbientScope_DeletesOwnersEdges()
    {
        await SeedNode("s", owner: null);
        await SeedNode("t", owner: null);
        await _innerStore.AddEdgesAsync([new GraphEdge
        {
            Id = "e1", SourceNodeId = "s", TargetNodeId = "t",
            Predicate = "relates_to", ChunkId = "c1", OwnerId = UserB
        }]);
        var store = SystemStore();

        var deleted = await store.DeleteEdgesByOwnerAsync(UserB);

        deleted.Should().BeEquivalentTo(["e1"]);
        (await _innerStore.GetEdgeCountAsync()).Should().Be(0);
    }

    private Task SeedNode(string id, string? owner) =>
        _innerStore.AddNodesAsync([new GraphNode { Id = id, Name = id, Type = "Fact", OwnerId = owner }]);

    private Task SeedTenantNode(string id, string? tenant, string? owner) =>
        _innerStore.AddNodesAsync([new GraphNode
        {
            Id = id, Name = id, Type = "Fact", OwnerId = owner, TenantId = tenant
        }]);

    private Task SeedEdge(string source, string target) =>
        _innerStore.AddEdgesAsync([new GraphEdge
        {
            Id = $"{source}->{target}", SourceNodeId = source, TargetNodeId = target,
            Predicate = "relates_to", ChunkId = "c1"
        }]);

    private TenantIsolatedGraphStore StoreFor(string userId) => StoreForScope(userId, tenantId: null);

    private TenantIsolatedGraphStore StoreForTenant(string userId, string tenantId) =>
        StoreForScope(userId, tenantId);

    private TenantIsolatedGraphStore StoreForScope(string userId, string? tenantId)
    {
        var scope = Mock.Of<IKnowledgeScope>(s => s.UserId == userId && s.TenantId == tenantId);
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
