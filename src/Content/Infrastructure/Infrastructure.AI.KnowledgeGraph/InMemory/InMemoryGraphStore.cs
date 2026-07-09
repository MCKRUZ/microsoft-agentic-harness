using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IKnowledgeGraphStore"/> for development
/// and unit testing. Data is stored in <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and is lost on process restart.
/// </summary>
/// <remarks>
/// This backend is registered with keyed DI key <c>"in_memory"</c> and is the default
/// for development environments. For production, use <c>"postgresql"</c> or <c>"neo4j"</c>.
/// Thread-safe for concurrent reads and writes within a single process.
/// </remarks>
public sealed class InMemoryGraphStore : IKnowledgeGraphStore
{
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new();
    private readonly ConcurrentDictionary<string, GraphEdge> _edges = new();
    private readonly ILogger<InMemoryGraphStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryGraphStore"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording graph operations.</param>
    public InMemoryGraphStore(ILogger<InMemoryGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default)
    {
        foreach (var rawNode in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Canonicalize owner/tenant on write so the case-sensitive owner filter below matches the
            // case-insensitive authorization gate. The decorator normally pre-canonicalizes, but this
            // backend is also used directly (dev + tests), so it must guarantee canonical storage itself.
            var node = rawNode with
            {
                OwnerId = ScopeIdentity.Canonicalize(rawNode.OwnerId),
                TenantId = ScopeIdentity.Canonicalize(rawNode.TenantId)
            };
            _nodes.AddOrUpdate(
                node.Id,
                node,
                (_, existing) => existing with
                {
                    ChunkIds = existing.ChunkIds
                        .Concat(node.ChunkIds)
                        .Distinct()
                        .ToList(),
                    Properties = MergeProperties(existing.Properties, node.Properties)
                });
        }

        _logger.LogDebug("Added/merged {Count} nodes, total: {Total}", nodes.Count, _nodes.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Canonicalize owner/tenant on write (see AddNodesAsync) so the owner-edge erasure sweep
            // below matches regardless of the casing the caller supplied.
            _edges.TryAdd(edge.Id, edge with
            {
                OwnerId = ScopeIdentity.Canonicalize(edge.OwnerId),
                TenantId = ScopeIdentity.Canonicalize(edge.TenantId)
            });
        }

        _logger.LogDebug("Added {Count} edges, total: {Total}", edges.Count, _edges.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        _nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string> { nodeId };
        var frontier = new HashSet<string> { nodeId };

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var nextFrontier = new HashSet<string>();
            foreach (var current in frontier)
            {
                foreach (var edge in _edges.Values)
                {
                    if (edge.SourceNodeId == current && visited.Add(edge.TargetNodeId))
                        nextFrontier.Add(edge.TargetNodeId);
                    if (edge.TargetNodeId == current && visited.Add(edge.SourceNodeId))
                        nextFrontier.Add(edge.SourceNodeId);
                }
            }

            frontier = nextFrontier;
            if (frontier.Count == 0) break;
        }

        visited.Remove(nodeId);
        var neighbors = visited
            .Where(id => _nodes.ContainsKey(id))
            .Select(id => _nodes[id])
            .ToList();

        return Task.FromResult<IReadOnlyList<GraphNode>>(neighbors);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var nodeIdSet = nodeIds.ToHashSet();
        var triplets = _edges.Values
            .Where(e => nodeIdSet.Contains(e.SourceNodeId) || nodeIdSet.Contains(e.TargetNodeId))
            .Where(e => _nodes.ContainsKey(e.SourceNodeId) && _nodes.ContainsKey(e.TargetNodeId))
            .Select(e => new GraphTriplet
            {
                Source = _nodes[e.SourceNodeId],
                Edge = e,
                Target = _nodes[e.TargetNodeId]
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<GraphTriplet>>(triplets);
    }

    /// <inheritdoc />
    public Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_nodes.ContainsKey(nodeId));
    }

    /// <inheritdoc />
    public Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        _nodes.TryRemove(nodeId, out _);
        var edgesToRemove = _edges.Values
            .Where(e => e.SourceNodeId == nodeId || e.TargetNodeId == nodeId)
            .Select(e => e.Id)
            .ToList();

        foreach (var edgeId in edgesToRemove)
            _edges.TryRemove(edgeId, out _);

        _logger.LogDebug(
            "Deleted node {NodeId} and {EdgeCount} connected edges",
            nodeId, edgesToRemove.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default)
    {
        _edges.TryRemove(edgeId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<NodeDeletionResult> DeleteNodesAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0)
            return Task.FromResult(NodeDeletionResult.Empty);

        var idSet = nodeIds.ToHashSet(StringComparer.Ordinal);
        var deletedNodeIds = new List<string>(idSet.Count);
        foreach (var nodeId in idSet)
        {
            if (_nodes.TryRemove(nodeId, out _))
                deletedNodeIds.Add(nodeId);
        }

        var cascadeEdgeIds = _edges.Values
            .Where(e => idSet.Contains(e.SourceNodeId) || idSet.Contains(e.TargetNodeId))
            .Select(e => e.Id)
            .ToList();

        var deletedEdgeIds = new List<string>(cascadeEdgeIds.Count);
        foreach (var edgeId in cascadeEdgeIds)
        {
            if (_edges.TryRemove(edgeId, out _))
                deletedEdgeIds.Add(edgeId);
        }

        _logger.LogDebug(
            "Deleted {NodeCount} of {Requested} nodes and {EdgeCount} connected edges",
            deletedNodeIds.Count, nodeIds.Count, deletedEdgeIds.Count);

        return Task.FromResult(new NodeDeletionResult
        {
            DeletedNodeIds = deletedNodeIds,
            DeletedEdgeIds = deletedEdgeIds
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> DeleteEdgesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        // Canonicalize the incoming owner before the exact match: stored identity is already
        // canonical (see AddEdgesAsync), so both sides are normalized and Ordinal is correct.
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);

        // A null canonical owner denotes an ABSENT/global owner, never an erasable subject. Without
        // this guard, string.Equals(record.OwnerId, null) would be true for every owner-null record
        // (the entire shared corpus/learnings/skill-memory), so an empty/whitespace erase request
        // would mass-delete the shared graph. Match nothing instead. (SQL backends already no-match
        // on null via SQL null semantics; this keeps all three backends in agreement.)
        if (canonicalOwner is null)
            return Task.FromResult<IReadOnlyList<string>>([]);

        var ownedEdgeIds = _edges.Values
            .Where(e => string.Equals(e.OwnerId, canonicalOwner, StringComparison.Ordinal))
            .Select(e => e.Id)
            .ToList();

        var deleted = new List<string>(ownedEdgeIds.Count);
        foreach (var edgeId in ownedEdgeIds)
        {
            if (_edges.TryRemove(edgeId, out _))
                deleted.Add(edgeId);
        }

        _logger.LogDebug("Deleted {Count} edges owned by {OwnerId}", deleted.Count, ownerId);
        return Task.FromResult<IReadOnlyList<string>>(deleted);
    }

    /// <inheritdoc />
    public Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_nodes.Count);
    }

    /// <inheritdoc />
    public Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_edges.Count);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        // Canonicalize the incoming owner before the exact match (see DeleteEdgesByOwnerAsync).
        var canonicalOwner = ScopeIdentity.Canonicalize(ownerId);

        // A null canonical owner is absent/global, never a queryable subject — match nothing rather
        // than equating null with every owner-null (shared) record. See DeleteEdgesByOwnerAsync.
        if (canonicalOwner is null)
            return Task.FromResult<IReadOnlyList<GraphNode>>([]);

        var owned = _nodes.Values
            .Where(n => string.Equals(n.OwnerId, canonicalOwner, StringComparison.Ordinal))
            .ToList();

        return Task.FromResult<IReadOnlyList<GraphNode>>(owned);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<GraphNode>>(_nodes.Values.ToList());
    }

    private static IReadOnlyDictionary<string, string> MergeProperties(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string> incoming)
    {
        var merged = new Dictionary<string, string>(existing);
        foreach (var kvp in incoming)
            merged[kvp.Key] = kvp.Value;
        return merged;
    }
}
