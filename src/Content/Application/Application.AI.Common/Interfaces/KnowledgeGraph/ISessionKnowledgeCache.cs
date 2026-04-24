using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Session-scoped fast cache for knowledge entries. Provides sub-millisecond
/// retrieval for facts learned during the current conversation, with background
/// flush to the permanent knowledge graph at session end.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <c>Scoped</c> lifetime so each request/session gets its own
/// isolated cache. The cache is in-memory only and is discarded when the scope ends
/// unless <see cref="FlushToGraphAsync"/> is called.
/// </para>
/// <para>
/// Search uses case-insensitive substring matching on node names and content.
/// For production, consider upgrading to embedding-based similarity search.
/// </para>
/// </remarks>
public interface ISessionKnowledgeCache
{
    /// <summary>
    /// Adds a graph node to the session cache, indexed by its ID and name.
    /// </summary>
    /// <param name="node">The node to cache.</param>
    void Add(GraphNode node);

    /// <summary>
    /// Searches the cache for nodes matching the query via substring match.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum results to return.</param>
    /// <returns>Matching nodes from the session cache.</returns>
    IReadOnlyList<GraphNode> Search(string query, int maxResults = 5);

    /// <summary>
    /// Removes a node from the session cache by ID.
    /// </summary>
    /// <param name="nodeId">The node ID to remove.</param>
    /// <returns><c>true</c> if the node was found and removed; otherwise <c>false</c>.</returns>
    bool Remove(string nodeId);

    /// <summary>
    /// Flushes all cached nodes to the permanent knowledge graph store.
    /// Called at session end to persist learned facts.
    /// </summary>
    /// <param name="graphStore">The graph store to flush to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task FlushToGraphAsync(
        IKnowledgeGraphStore graphStore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of entries currently in the cache.
    /// </summary>
    int Count { get; }
}
