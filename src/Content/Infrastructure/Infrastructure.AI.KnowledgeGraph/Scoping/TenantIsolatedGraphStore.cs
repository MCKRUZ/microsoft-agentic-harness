using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Decorator over <see cref="IKnowledgeGraphStore"/> that enforces multi-tenant
/// knowledge isolation by validating the current <see cref="IKnowledgeScope"/>
/// before delegating to the inner store.
/// </summary>
/// <remarks>
/// <para>
/// All operations validate tenant access via <see cref="IKnowledgeScopeValidator"/>
/// before delegating. When access is denied, mutations are silently skipped and
/// queries return empty results, preventing cross-tenant data leakage.
/// </para>
/// <para>
/// Registered conditionally: only when <c>GraphRagConfig.MultiTenantIsolation</c>
/// is <c>true</c>. In single-tenant mode, the inner store is used directly.
/// </para>
/// </remarks>
public sealed class TenantIsolatedGraphStore : IKnowledgeGraphStore
{
    private readonly IKnowledgeGraphStore _inner;
    private readonly IKnowledgeScope _scope;
    private readonly IKnowledgeScopeValidator _validator;
    private readonly ILogger<TenantIsolatedGraphStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantIsolatedGraphStore"/> class.
    /// </summary>
    /// <param name="inner">The underlying graph store to delegate to.</param>
    /// <param name="scope">The current knowledge scope for tenant identification.</param>
    /// <param name="validator">Validates access permissions.</param>
    /// <param name="logger">Logger for recording access decisions.</param>
    public TenantIsolatedGraphStore(
        IKnowledgeGraphStore inner,
        IKnowledgeScope scope,
        IKnowledgeScopeValidator validator,
        ILogger<TenantIsolatedGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _scope = scope;
        _validator = validator;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.CompletedTask;
        return _inner.AddNodesAsync(nodes, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.CompletedTask;
        return _inner.AddEdgesAsync(edges, cancellationToken);
    }

    /// <inheritdoc />
    public Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult<GraphNode?>(null);
        return _inner.GetNodeAsync(nodeId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult<IReadOnlyList<GraphNode>>([]);
        return _inner.GetNeighborsAsync(nodeId, maxDepth, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult<IReadOnlyList<GraphTriplet>>([]);
        return _inner.GetTripletsAsync(nodeIds, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult(false);
        return _inner.NodeExistsAsync(nodeId, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.CompletedTask;
        return _inner.DeleteNodeAsync(nodeId, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.CompletedTask;
        return _inner.DeleteEdgeAsync(edgeId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult(0);
        return _inner.GetNodeCountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAccess()) return Task.FromResult(0);
        return _inner.GetEdgeCountAsync(cancellationToken);
    }

    private bool HasAccess()
    {
        var allowed = _validator.ValidateAccess(_scope, _scope.TenantId, _scope.DatasetId);
        if (!allowed)
        {
            _logger.LogWarning(
                "Tenant isolation: access denied for User={UserId}, Tenant={TenantId}, Dataset={DatasetId}",
                _scope.UserId, _scope.TenantId, _scope.DatasetId);
        }

        return allowed;
    }
}
