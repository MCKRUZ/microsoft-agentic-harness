using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Scoped in-memory <see cref="ISessionKnowledgeCache"/> using
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for thread-safe session-local
/// knowledge caching. Discarded when the DI scope ends unless flushed.
/// </summary>
public sealed class InMemorySessionCache : ISessionKnowledgeCache
{
    private readonly ConcurrentDictionary<string, GraphNode> _entries = new();

    /// <inheritdoc />
    public void Add(GraphNode node)
    {
        _entries[node.Id] = node;
    }

    /// <inheritdoc />
    public IReadOnlyList<GraphNode> Search(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return _entries.Values
            .Where(node => terms.Any(t =>
                node.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                node.Type.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                node.Properties.Values.Any(v => v.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Take(maxResults)
            .ToList();
    }

    /// <inheritdoc />
    public bool Remove(string nodeId)
    {
        return _entries.TryRemove(nodeId, out _);
    }

    /// <inheritdoc />
    public async Task FlushToGraphAsync(
        IKnowledgeGraphStore graphStore,
        CancellationToken cancellationToken = default)
    {
        var nodes = _entries.Values.ToList();
        if (nodes.Count > 0)
            await graphStore.AddNodesAsync(nodes, cancellationToken);
    }

    /// <inheritdoc />
    public int Count => _entries.Count;
}
