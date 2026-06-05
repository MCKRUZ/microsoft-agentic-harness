using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Decorator over <see cref="IKnowledgeGraphStore"/> that enforces multi-tenant
/// knowledge isolation by filtering every record against the current caller's
/// <see cref="IKnowledgeScope"/> before it is returned, and rejecting writes the
/// caller is not entitled to make.
/// </summary>
/// <remarks>
/// <para>
/// Isolation is <strong>per record, by owner</strong> — not an all-or-nothing gate. A node is
/// visible when it is unowned (<see cref="GraphNode.OwnerId"/> is <see langword="null"/>, i.e.
/// shared/ingested corpus) or when the caller owns it
/// (<see cref="IKnowledgeScopeValidator.CanAccessDataset"/>). A user therefore only ever sees
/// their own remembered facts plus the shared corpus, never another user's memory — even within
/// the same tenant. True tenant-level corpus partitioning requires a <c>TenantId</c> on the node
/// model and is deferred to a later change; until then, unowned corpus is shared across tenants.
/// </para>
/// <para>
/// Registered as a <c>Singleton</c> wrapping the singleton backend, so it cannot capture the
/// scoped <see cref="IKnowledgeScope"/>. It resolves the caller's scope <em>per operation</em>
/// from <see cref="IAmbientRequestScope"/> (established by <c>AmbientRequestScopeBehavior</c>).
/// When no request scope is in flight — background retention/learnings or the post-turn flush —
/// the scope is <see langword="null"/> and the store grants full access, so system work sees the
/// whole graph.
/// </para>
/// <para>
/// Registered conditionally: only when <c>GraphRagConfig.MultiTenantIsolation</c> is <c>true</c>.
/// In single-tenant mode the inner store is used directly.
/// </para>
/// </remarks>
public sealed class TenantIsolatedGraphStore : IKnowledgeGraphStore
{
    private readonly IKnowledgeGraphStore _inner;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IKnowledgeScopeValidator _validator;
    private readonly ILogger<TenantIsolatedGraphStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantIsolatedGraphStore"/> class.
    /// </summary>
    /// <param name="inner">The underlying graph store to delegate to.</param>
    /// <param name="ambientScope">Bridge to the in-flight request scope for per-op identity.</param>
    /// <param name="validator">Validates per-record access permissions.</param>
    /// <param name="logger">Logger for recording access decisions.</param>
    public TenantIsolatedGraphStore(
        IKnowledgeGraphStore inner,
        IAmbientRequestScope ambientScope,
        IKnowledgeScopeValidator validator,
        ILogger<TenantIsolatedGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _ambientScope = ambientScope;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>
    /// The caller's scope for the operation in flight, or <see langword="null"/> for
    /// background/system work running outside any request scope (full access).
    /// </summary>
    private IKnowledgeScope? CurrentScope =>
        _ambientScope.Current?.GetService<IKnowledgeScope>();

    /// <inheritdoc />
    public Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null) return _inner.AddNodesAsync(nodes, cancellationToken);

        // Fresh nodes arrive unowned and are stamped with the owner by the inner
        // ComplianceAwareGraphStore; only reject nodes that already carry a foreign owner.
        var permitted = nodes.Where(n => CanAccess(n.OwnerId, scope)).ToList();
        if (permitted.Count != nodes.Count)
        {
            _logger.LogWarning(
                "Tenant isolation: rejected {Rejected} of {Total} node writes for User={UserId}",
                nodes.Count - permitted.Count, nodes.Count, scope.UserId);
        }

        return permitted.Count == 0
            ? Task.CompletedTask
            : _inner.AddNodesAsync(permitted, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null) return _inner.AddEdgesAsync(edges, cancellationToken);

        var permitted = edges.Where(e => CanAccess(e.OwnerId, scope)).ToList();
        return permitted.Count == 0
            ? Task.CompletedTask
            : _inner.AddEdgesAsync(permitted, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await _inner.GetNodeAsync(nodeId, cancellationToken);
        var scope = CurrentScope;
        if (node is null || scope is null) return node;
        return CanAccess(node.OwnerId, scope) ? node : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null)
            return await _inner.GetNeighborsAsync(nodeId, maxDepth, cancellationToken);

        // Access-check the traversal seed itself: it never appears in the result set, so without
        // this guard a caller could seed the traversal on a foreign private node and learn which
        // shared entities it connects to. (GetTripletsAsync needs no seed guard — the seed always
        // appears as a triplet endpoint and is dropped by the both-endpoints-visible filter.)
        var seed = await _inner.GetNodeAsync(nodeId, cancellationToken);
        if (seed is null || !CanAccess(seed.OwnerId, scope))
            return [];

        var neighbors = await _inner.GetNeighborsAsync(nodeId, maxDepth, cancellationToken);
        return neighbors.Where(n => CanAccess(n.OwnerId, scope)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var triplets = await _inner.GetTripletsAsync(nodeIds, cancellationToken);
        var scope = CurrentScope;
        if (scope is null) return triplets;

        // A triplet is visible only when both endpoints are visible to the caller.
        return triplets
            .Where(t => CanAccess(t.Source.OwnerId, scope) && CanAccess(t.Target.OwnerId, scope))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null) return await _inner.NodeExistsAsync(nodeId, cancellationToken);

        // Existence must mirror visibility: a node the caller cannot read does not exist for them.
        // This trades the backend's cheap existence probe for a full node fetch to read OwnerId;
        // acceptable for typical use, but bulk ingestion dedup on a large backend would want an
        // owner-aware NodeExists primitive on IKnowledgeGraphStore (deferred with the TenantId work).
        var node = await _inner.GetNodeAsync(nodeId, cancellationToken);
        return node is not null && CanAccess(node.OwnerId, scope);
    }

    /// <inheritdoc />
    public async Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null)
        {
            await _inner.DeleteNodeAsync(nodeId, cancellationToken);
            return;
        }

        var node = await _inner.GetNodeAsync(nodeId, cancellationToken);
        if (node is null) return;
        if (!CanAccess(node.OwnerId, scope))
        {
            _logger.LogWarning(
                "Tenant isolation: blocked delete of foreign node {NodeId} for User={UserId}",
                nodeId, scope.UserId);
            return;
        }

        await _inner.DeleteNodeAsync(nodeId, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
        // Edges expose no owner lookup; edge-level isolation is deferred with the TenantId model
        // change. Edges carry no user-memory content, so this delegates unfiltered.
        => _inner.DeleteEdgeAsync(edgeId, cancellationToken);

    /// <inheritdoc />
    public async Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope;
        if (scope is null) return await _inner.GetNodeCountAsync(cancellationToken);

        // Count must reflect what the caller can actually see, which the backend's aggregate count
        // cannot express. This materializes all nodes to filter by owner — fine for the in-memory
        // store and modest graphs, but a scaling cliff on a large Neo4j/PostgreSQL backend. The
        // proper fix is an owner-scoped count primitive on IKnowledgeGraphStore (deferred with the
        // TenantId work); until then, prefer not to poll this on large multi-tenant graphs.
        var all = await _inner.GetAllNodesAsync(cancellationToken);
        return all.Count(n => CanAccess(n.OwnerId, scope));
    }

    /// <inheritdoc />
    public Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetEdgeCountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        // Results are filtered against the CALLER's scope, not the requested ownerId. A privileged
        // operation that must enumerate another user's nodes — e.g. right-to-erasure by owner — has
        // to run system-scoped (no ambient request scope, full access), which is exactly how
        // RetentionEnforcementService and the erasure orchestrator are invoked. A user-scoped caller
        // only ever sees their own owned nodes here.
        var nodes = await _inner.GetNodesByOwnerAsync(ownerId, cancellationToken);
        return Filter(nodes);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(
        CancellationToken cancellationToken = default)
    {
        var nodes = await _inner.GetAllNodesAsync(cancellationToken);
        return Filter(nodes);
    }

    private IReadOnlyList<GraphNode> Filter(IReadOnlyList<GraphNode> nodes)
    {
        var scope = CurrentScope;
        if (scope is null) return nodes;
        return nodes.Where(n => CanAccess(n.OwnerId, scope)).ToList();
    }

    /// <summary>
    /// A record is accessible when it is unowned (shared corpus) or the caller owns it.
    /// </summary>
    private bool CanAccess(string? ownerId, IKnowledgeScope scope)
        => ownerId is null || _validator.CanAccessDataset(scope, ownerId);
}
