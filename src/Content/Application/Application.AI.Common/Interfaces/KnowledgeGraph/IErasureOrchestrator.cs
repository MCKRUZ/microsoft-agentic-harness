using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Coordinates right-to-erasure across all storage layers: graph nodes/edges,
/// feedback weights, and vector embeddings. Returns an <see cref="ErasureReceipt"/>
/// as proof of compliance.
/// </summary>
public interface IErasureOrchestrator
{
    /// <summary>Erase all data owned by the specified owner (user/tenant).</summary>
    Task<ErasureReceipt> EraseByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Erase specific nodes and all their associated data.</summary>
    Task<ErasureReceipt> EraseByNodeIdsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erase the supplied nodes and all their associated data <em>without re-fetching them</em>
    /// from the graph store. This is the primitive the retention enforcer uses: it already holds
    /// the full expired <see cref="GraphNode"/> instances (including their
    /// <see cref="GraphNode.ChunkIds"/>) from an unfiltered scan, and the compliance-aware store
    /// returns <see langword="null"/> for expired nodes on re-fetch — so
    /// <see cref="EraseByNodeIdsAsync"/> would collect zero chunk IDs and silently skip the
    /// derived-content (vector/BM25) purge. Passing the nodes directly preserves their chunk IDs
    /// so derived content is erased.
    /// </summary>
    /// <remarks>
    /// <strong>Callers MUST pass authentic, store-sourced <see cref="GraphNode"/> instances</strong>
    /// (obtained from <see cref="IKnowledgeGraphStore.GetAllNodesAsync"/> /
    /// <see cref="IKnowledgeGraphStore.GetNodesByOwnerAsync"/> / <see cref="IKnowledgeGraphStore.GetNodeAsync"/>).
    /// This method trusts each node's <see cref="GraphNode.ChunkIds"/> to derive the document IDs it
    /// purges from the vector and BM25 stores; it does NOT re-validate the node↔chunk association.
    /// Passing fabricated or caller-mutated <see cref="GraphNode.ChunkIds"/> would delete another
    /// document's derived content. Never call this with caller-constructed nodes — use
    /// <see cref="EraseByNodeIdsAsync"/> (which re-fetches) when you hold only IDs.
    /// </remarks>
    /// <param name="nodes">
    /// The store-sourced nodes to erase, carrying their chunk references for derived-content cleanup.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ErasureReceipt> EraseByNodesAsync(IReadOnlyList<GraphNode> nodes, CancellationToken cancellationToken = default);
}
