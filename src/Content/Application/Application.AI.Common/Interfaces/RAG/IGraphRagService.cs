using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Provides knowledge graph-based retrieval using Microsoft GraphRAG patterns.
/// Supports both global search (community-level map-reduce synthesis over the entire
/// corpus) and local search (entity-neighborhood retrieval for specific questions).
/// GraphRAG excels at answering broad thematic queries that traditional vector search
/// handles poorly.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item><see cref="IndexCorpusAsync"/>: Extract entities and relationships from chunks
///         using an LLM, build a knowledge graph, detect communities via Leiden algorithm,
///         and generate community summaries at multiple levels.</item>
///   <item><see cref="GlobalSearchAsync"/>: Map-reduce across community summaries at the
///         specified level. Higher levels produce more abstract answers; lower levels
///         produce more detailed answers. Returns assembled context, not raw chunks.</item>
///   <item><see cref="LocalSearchAsync"/>: Starting from entities mentioned in the query,
///         traverse the graph neighborhood to find relevant chunks. Returns results
///         compatible with the standard reranking pipeline.</item>
///   <item>Indexing is expensive (many LLM calls). Use <see cref="IRagModelRouter"/> to
///         route entity extraction to an economy-tier model.</item>
///   <item>Graph storage: Use a graph database (Neo4j, Cosmos DB Gremlin) or an in-memory
///         representation for small corpora.</item>
/// </list>
/// </remarks>
public interface IGraphRagService
{
    /// <summary>
    /// Indexes a corpus of chunks into the knowledge graph. Extracts entities and
    /// relationships, builds the graph, and generates community summaries.
    /// </summary>
    /// <param name="chunks">The document chunks to index. May span multiple documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IndexCorpusAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a global search using community summaries at the specified level.
    /// Best for broad, thematic queries (e.g., "What are the main themes in this corpus?").
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="communityLevel">
    /// The community hierarchy level to search (0 = most granular, higher = more abstract).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Assembled context synthesized from community summaries.</returns>
    Task<RagAssembledContext> GlobalSearchAsync(
        string query,
        int communityLevel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a local search by traversing entity neighborhoods in the knowledge graph.
    /// Best for specific queries about named entities or relationships.
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results from graph neighborhood traversal, scored by relevance.</returns>
    Task<IReadOnlyList<RetrievalResult>> LocalSearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}
