using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Scoping;

/// <summary>
/// Tests for <see cref="TenantIsolatedGraphStore"/> — decorator that blocks
/// cross-tenant access to the underlying graph store.
/// </summary>
public sealed class TenantIsolatedGraphStoreTests
{
    private readonly InMemoryGraphStore _innerStore;
    private readonly Mock<IKnowledgeScope> _scope;
    private readonly Mock<IKnowledgeScopeValidator> _validator;

    public TenantIsolatedGraphStoreTests()
    {
        _innerStore = new InMemoryGraphStore(NullLogger<InMemoryGraphStore>.Instance);
        _scope = new Mock<IKnowledgeScope>();
        _scope.Setup(s => s.TenantId).Returns("t1");
        _scope.Setup(s => s.DatasetId).Returns("d1");
        _validator = new Mock<IKnowledgeScopeValidator>();
    }

    [Fact]
    public async Task AddNodes_AccessAllowed_DelegatesToInner()
    {
        AllowAccess();
        var store = CreateStore();
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Entity" };

        await store.AddNodesAsync([node]);

        (await _innerStore.GetNodeCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AddNodes_AccessDenied_SkipsSilently()
    {
        DenyAccess();
        var store = CreateStore();
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Entity" };

        await store.AddNodesAsync([node]);

        (await _innerStore.GetNodeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetNode_AccessAllowed_ReturnsNode()
    {
        AllowAccess();
        var store = CreateStore();
        await _innerStore.AddNodesAsync([new GraphNode { Id = "n1", Name = "Test", Type = "Entity" }]);

        var result = await store.GetNodeAsync("n1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetNode_AccessDenied_ReturnsNull()
    {
        await _innerStore.AddNodesAsync([new GraphNode { Id = "n1", Name = "Test", Type = "Entity" }]);
        DenyAccess();
        var store = CreateStore();

        var result = await store.GetNodeAsync("n1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetNodeCount_AccessDenied_ReturnsZero()
    {
        await _innerStore.AddNodesAsync([new GraphNode { Id = "n1", Name = "Test", Type = "Entity" }]);
        DenyAccess();
        var store = CreateStore();

        (await store.GetNodeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NodeExists_AccessDenied_ReturnsFalse()
    {
        await _innerStore.AddNodesAsync([new GraphNode { Id = "n1", Name = "Test", Type = "Entity" }]);
        DenyAccess();
        var store = CreateStore();

        (await store.NodeExistsAsync("n1")).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteNode_AccessDenied_DoesNotDelete()
    {
        AllowAccess();
        await _innerStore.AddNodesAsync([new GraphNode { Id = "n1", Name = "Test", Type = "Entity" }]);
        DenyAccess();
        var store = CreateStore();

        await store.DeleteNodeAsync("n1");

        // Verify inner store still has the node (use inner directly)
        (await _innerStore.GetNodeAsync("n1")).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNeighbors_AccessDenied_ReturnsEmpty()
    {
        DenyAccess();
        var store = CreateStore();

        var result = await store.GetNeighborsAsync("n1");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTriplets_AccessDenied_ReturnsEmpty()
    {
        DenyAccess();
        var store = CreateStore();

        var result = await store.GetTripletsAsync(["n1"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AddEdges_AccessDenied_SkipsSilently()
    {
        DenyAccess();
        var store = CreateStore();
        var edge = new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "uses", ChunkId = "c1"
        };

        await store.AddEdgesAsync([edge]);

        (await _innerStore.GetEdgeCountAsync()).Should().Be(0);
    }

    private TenantIsolatedGraphStore CreateStore() =>
        new(_innerStore, _scope.Object, _validator.Object,
            NullLogger<TenantIsolatedGraphStore>.Instance);

    private void AllowAccess() =>
        _validator
            .Setup(v => v.ValidateAccess(It.IsAny<IKnowledgeScope>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(true);

    private void DenyAccess() =>
        _validator
            .Setup(v => v.ValidateAccess(It.IsAny<IKnowledgeScope>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(false);
}
