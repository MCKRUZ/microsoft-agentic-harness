using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Audit;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

public sealed class ComplianceAwareGraphStoreTests
{
    private readonly InMemoryGraphStore _innerStore;
    private readonly Mock<IMemoryAuditSink> _auditSink;
    private readonly Mock<IKnowledgeScope> _scope;
    private readonly Mock<IRetentionPolicyProvider> _retentionProvider;
    private readonly FakeTimeProvider _timeProvider;
    private readonly ComplianceAwareGraphStore _store;

    public ComplianceAwareGraphStoreTests()
    {
        _innerStore = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        _auditSink = new Mock<IMemoryAuditSink>();
        _scope = new Mock<IKnowledgeScope>();
        _scope.Setup(s => s.UserId).Returns("user-1");
        _retentionProvider = new Mock<IRetentionPolicyProvider>();
        _retentionProvider.Setup(r => r.GetPolicy(It.IsAny<string>()))
            .Returns(new RetentionPolicy
            {
                EntityType = "Fact",
                RetentionPeriod = TimeSpan.FromDays(365)
            });
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        _store = new ComplianceAwareGraphStore(
            _innerStore,
            _auditSink.Object,
            _scope.Object,
            _retentionProvider.Object,
            _timeProvider,
            Mock.Of<ILogger<ComplianceAwareGraphStore>>());
    }

    [Fact]
    public async Task AddNodes_StampsCreatedAtAndExpiresAtAndOwnerId()
    {
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Fact" };

        await _store.AddNodesAsync([node]);

        var stored = await _innerStore.GetNodeAsync("n1");
        stored!.CreatedAt.Should().Be(_timeProvider.GetUtcNow());
        stored.ExpiresAt.Should().Be(_timeProvider.GetUtcNow().AddDays(365));
        stored.OwnerId.Should().Be("user-1");
    }

    [Fact]
    public async Task AddNodes_IndefiniteRetention_ExpiresAtIsNull()
    {
        _retentionProvider.Setup(r => r.GetPolicy("Concept"))
            .Returns(new RetentionPolicy { EntityType = "Concept", RetentionPeriod = TimeSpan.Zero, AllowIndefinite = true });
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Concept" };

        await _store.AddNodesAsync([node]);

        var stored = await _innerStore.GetNodeAsync("n1");
        stored!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task AddNodes_EmitsRememberAuditEvent()
    {
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Fact" };

        await _store.AddNodesAsync([node]);

        _auditSink.Verify(s => s.EmitAsync(
            It.Is<MemoryAuditEvent>(e =>
                e.Action == MemoryAuditAction.Remember &&
                e.AffectedNodeIds!.Contains("n1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNodeAsync_ExpiredNode_ReturnsNull()
    {
        var node = new GraphNode
        {
            Id = "n1", Name = "Test", Type = "Fact",
            CreatedAt = _timeProvider.GetUtcNow().AddDays(-400),
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(-35)
        };
        await _innerStore.AddNodesAsync([node]);

        var result = await _store.GetNodeAsync("n1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetNodeAsync_ValidNode_ReturnsNode()
    {
        var node = new GraphNode
        {
            Id = "n1", Name = "Test", Type = "Fact",
            CreatedAt = _timeProvider.GetUtcNow(),
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(365)
        };
        await _innerStore.AddNodesAsync([node]);

        var result = await _store.GetNodeAsync("n1");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetNodeAsync_NullExpiresAt_ReturnsNode()
    {
        var node = new GraphNode
        {
            Id = "n1", Name = "Test", Type = "Concept",
            CreatedAt = _timeProvider.GetUtcNow(),
            ExpiresAt = null
        };
        await _innerStore.AddNodesAsync([node]);

        var result = await _store.GetNodeAsync("n1");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteNodeAsync_EmitsForgetAuditEvent()
    {
        var node = new GraphNode { Id = "n1", Name = "Test", Type = "Fact" };
        await _innerStore.AddNodesAsync([node]);

        await _store.DeleteNodeAsync("n1");

        _auditSink.Verify(s => s.EmitAsync(
            It.Is<MemoryAuditEvent>(e =>
                e.Action == MemoryAuditAction.Forget &&
                e.AffectedNodeIds!.Contains("n1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>
/// Fake TimeProvider for deterministic testing.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}
