using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
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
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            _edges.TryAdd(edge.Id, edge);
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
    public Task<int> GetNodeCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_nodes.Count);
    }

    /// <inheritdoc />
    public Task<int> GetEdgeCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_edges.Count);
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
