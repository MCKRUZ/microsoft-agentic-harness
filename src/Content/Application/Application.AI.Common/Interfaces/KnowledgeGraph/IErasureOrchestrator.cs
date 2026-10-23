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

    /// <summary>
    /// Removes nodes whose retention period has passed (#901): the nodes, the edges touching them and their feedback
    /// weights, but not the document content behind their chunk references.
    /// </summary>
    /// <remarks>
    /// Erasure purges derived content by document: every chunk of a node's source document goes, which is right when a
    /// person or a document is being erased and wrong when one node has merely aged. That document is still the corpus
    /// for every other node extracted from it, and it has its own lifetime.
    /// </remarks>
    /// <param name="nodes">The store-sourced nodes to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ErasureReceipt> ExpireNodesAsync(IReadOnlyList<GraphNode> nodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one source document from the corpus (#901): its chunks from every vector and full-text collection, and the
    /// knowledge extracted from it in the knowledge graph and in the GraphRAG corpus graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node only this document produced is deleted with its edges and feedback weights. A node another document also
    /// produced keeps that document's chunk references and loses this one's, so shared knowledge survives. An edge drawn
    /// from this document's text goes. In the corpus graph, every community a removed or changed node belongs to is
    /// deleted, because its summary was written from text that may repeat the document.
    /// </para>
    /// <para>
    /// A document id is derived from its source alone, so this reaches every collection the document was loaded into.
    /// Run it without an ambient request scope, as the erasure paths are, or a tenant-isolated graph shows it only the
    /// caller's tenant.
    /// </para>
    /// </remarks>
    /// <param name="documentId">The document to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What each store actually removed.</returns>
    Task<ErasureReceipt> RemoveDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
