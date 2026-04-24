using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Low-level storage abstraction for the knowledge graph, providing CRUD operations
/// on <see cref="GraphNode"/> and <see cref="GraphEdge"/> entities plus graph traversal
/// primitives. This is the backend abstraction with keyed DI for provider selection
/// (<c>"in_memory"</c>, <c>"postgresql"</c>, <c>"neo4j"</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is NOT the high-level RAG interface.</strong> For pipeline-level operations
/// (IndexCorpus, GlobalSearch, LocalSearch), use <see cref="RAG.IGraphRagService"/> which
/// consumes <c>IKnowledgeGraphStore</c> internally. This separation mirrors the
/// <see cref="RAG.IVectorStore"/> (low-level) vs <see cref="RAG.IHybridRetriever"/>
/// (high-level) pattern already established in the RAG pipeline.
/// </para>
/// <para>
/// <strong>Implementation guidance:</strong>
/// <list type="bullet">
///   <item>All mutations (add/delete) must be idempotent — repeated calls with the same
///         data should not create duplicates or throw.</item>
///   <item>Node deduplication is by <see cref="GraphNode.Id"/>. When a node with the same
///         ID already exists, implementations should merge <see cref="GraphNode.ChunkIds"/>
///         rather than overwriting.</item>
///   <item>Edge deduplication is by <see cref="GraphEdge.Id"/>. Duplicate edges are
///         silently ignored.</item>
///   <item>Graph traversal (<see cref="GetNeighborsAsync"/>) must respect
///         <paramref name="maxDepth"/> to prevent unbounded expansion.</item>
/// </list>
/// </para>
/// </remarks>
public interface IKnowledgeGraphStore
{
    /// <summary>
    /// Adds nodes to the knowledge graph. Existing nodes with the same ID are merged
    /// (chunk IDs combined, properties updated).
    /// </summary>
    /// <param name="nodes">The nodes to add or merge.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddNodesAsync(
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds edges to the knowledge graph. Duplicate edges (same ID) are silently ignored.
    /// </summary>
    /// <param name="edges">The edges to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddEdgesAsync(
        IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single node by its ID. Returns <c>null</c> if not found.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GraphNode?> GetNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all neighbor nodes reachable within <paramref name="maxDepth"/> hops
    /// from the specified node, following edges in either direction.
    /// </summary>
    /// <param name="nodeId">The starting node identifier.</param>
    /// <param name="maxDepth">Maximum traversal depth (1 = direct neighbors only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        string nodeId,
        int maxDepth = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves complete triplets (source → edge → target) for the specified node IDs.
    /// Returns all edges where any of the given nodes appear as source or target.
    /// </summary>
    /// <param name="nodeIds">The node identifiers to retrieve triplets for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GraphTriplet>> GetTripletsAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a node with the specified ID exists in the graph.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> NodeExistsAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a node and all edges connected to it (both incoming and outgoing).
    /// No-op if the node does not exist.
    /// </summary>
    /// <param name="nodeId">The node identifier to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single edge by its ID. No-op if the edge does not exist.
    /// </summary>
    /// <param name="edgeId">The edge identifier to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteEdgeAsync(
        string edgeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of nodes in the graph (optionally scoped by tenant/dataset
    /// when multi-tenant isolation is enabled).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetNodeCountAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of edges in the graph.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetEdgeCountAsync(
        CancellationToken cancellationToken = default);
}
