using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// Decorator over <see cref="IKnowledgeGraphStore"/> that enforces compliance:
/// stamps temporal metadata on writes, filters expired nodes on reads,
/// and emits audit events for all operations.
/// </summary>
/// <remarks>
/// Registered as a <c>Singleton</c> wrapping the singleton backend store, so it cannot
/// capture the scoped <see cref="IKnowledgeScope"/> at construction. Instead it resolves
/// the caller's scope <em>per operation</em> from <see cref="IAmbientRequestScope"/>, which
/// the MediatR pipeline establishes for the in-flight request (see
/// <c>AmbientRequestScopeBehavior</c>). When no request scope is in flight — background
/// retention/learnings work or the post-turn knowledge flush running after scope disposal —
/// <see cref="CurrentUserId"/> is <see langword="null"/>, and the existing <c>?? "system"</c>
/// / <c>?? ownerId</c> fallbacks attribute the operation to the system actor.
/// </remarks>
public sealed class ComplianceAwareGraphStore : IKnowledgeGraphStore
{
    private readonly IKnowledgeGraphStore _inner;
    private readonly IMemoryAuditSink _auditSink;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IRetentionPolicyProvider _retentionProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComplianceAwareGraphStore> _logger;

    public ComplianceAwareGraphStore(
        IKnowledgeGraphStore inner,
        IMemoryAuditSink auditSink,
        IAmbientRequestScope ambientScope,
        IRetentionPolicyProvider retentionProvider,
        TimeProvider timeProvider,
        ILogger<ComplianceAwareGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(retentionProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _auditSink = auditSink;
        _ambientScope = ambientScope;
        _retentionProvider = retentionProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The user ID of the caller in flight on the current async context, or
    /// <see langword="null"/> for background/system work outside any request scope.
    /// </summary>
    private string? CurrentUserId =>
        _ambientScope.Current?.GetService<IKnowledgeScope>()?.UserId;

    /// <inheritdoc />
    public async Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var ownerId = CurrentUserId;
        var stamped = nodes.Select(n => StampNode(n, now)).ToList();

        await _inner.AddNodesAsync(stamped, cancellationToken);

        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Action = MemoryAuditAction.Remember,
            ActorId = ownerId ?? "system",
            Timestamp = now,
            ScopeId = ownerId ?? "system",
            AffectedNodeIds = stamped.Select(n => n.Id).ToList()
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var stamped = edges.Select(e => e with
        {
            CreatedAt = e.CreatedAt ?? now
        }).ToList();

        await _inner.AddEdgesAsync(stamped, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await _inner.GetNodeAsync(nodeId, cancellationToken);
        if (node is null || IsExpired(node)) return null;

        await EmitRecallEvent([node], cancellationToken);
        return node;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        var neighbors = await _inner.GetNeighborsAsync(nodeId, maxDepth, cancellationToken);
        var valid = neighbors.Where(n => !IsExpired(n)).ToList();

        if (valid.Count > 0)
            await EmitRecallEvent(valid, cancellationToken);

        return valid;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var triplets = await _inner.GetTripletsAsync(nodeIds, cancellationToken);
        return triplets.Where(t => !IsExpired(t.Source) && !IsExpired(t.Target)).ToList();
    }

    /// <inheritdoc />
    public Task<bool> NodeExistsAsync(string nodeId, CancellationToken cancellationToken = default)
        => _inner.NodeExistsAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteNodeAsync(nodeId, cancellationToken);

        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Action = MemoryAuditAction.Forget,
            ActorId = CurrentUserId ?? "system",
            Timestamp = _timeProvider.GetUtcNow(),
            ScopeId = CurrentUserId ?? "system",
            AffectedNodeIds = [nodeId]
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteEdgeAsync(edgeId, cancellationToken);

        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Action = MemoryAuditAction.Forget,
            ActorId = CurrentUserId ?? "system",
            Timestamp = _timeProvider.GetUtcNow(),
            ScopeId = CurrentUserId ?? "system",
            AffectedEdgeIds = [edgeId]
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetNodeCountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetEdgeCountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
        => _inner.GetNodesByOwnerAsync(ownerId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(
        CancellationToken cancellationToken = default)
        => _inner.GetAllNodesAsync(cancellationToken);

    // Stamps temporal metadata only. OwnerId is intentionally NOT defaulted here: ownership is
    // authoritative from the writer (KnowledgeMemoryService sets OwnerId = the user; shared
    // subsystems — corpus ingestion, learnings, skill memory — leave it null on purpose so the
    // records stay globally visible). Defaulting a null owner to the current user would silently
    // privatize shared knowledge once TenantIsolatedGraphStore is active.
    private GraphNode StampNode(GraphNode node, DateTimeOffset now)
    {
        var policy = _retentionProvider.GetPolicy(node.Type);
        return node with
        {
            CreatedAt = node.CreatedAt ?? now,
            ExpiresAt = node.ExpiresAt ?? (policy.AllowIndefinite ? null : now + policy.RetentionPeriod)
        };
    }

    private bool IsExpired(GraphNode node)
        => node.ExpiresAt.HasValue && node.ExpiresAt.Value < _timeProvider.GetUtcNow();

    private async Task EmitRecallEvent(IReadOnlyList<GraphNode> nodes, CancellationToken ct)
    {
        await _auditSink.EmitAsync(new MemoryAuditEvent
        {
            EventId = Guid.NewGuid().ToString(),
            Action = MemoryAuditAction.Recall,
            ActorId = CurrentUserId ?? "system",
            Timestamp = _timeProvider.GetUtcNow(),
            ScopeId = CurrentUserId ?? "system",
            AffectedNodeIds = nodes.Select(n => n.Id).ToList(),
            ResultCount = nodes.Count
        }, ct);
    }
}
