namespace Domain.AI.RAG.Models;

/// <summary>
/// The result of retrieving a <see cref="DocumentChunk"/> from the vector store,
/// carrying the individual scoring components used in hybrid search. The dense score
/// comes from vector similarity, the sparse score from BM25 keyword matching, and
/// the fused score from Reciprocal Rank Fusion (RRF) combining both signals.
/// </summary>
public record RetrievalResult
{
    /// <summary>
    /// The retrieved chunk with its content, metadata, and embedding.
    /// </summary>
    public required DocumentChunk Chunk { get; init; }

    /// <summary>
    /// The cosine similarity score from dense vector search (0.0 to 1.0).
    /// Higher values indicate stronger semantic similarity to the query embedding.
    /// </summary>
    public required double DenseScore { get; init; }

    /// <summary>
    /// The BM25 score from sparse keyword matching.
    /// Captures lexical overlap that vector search may miss (exact terms,
    /// acronyms, identifiers). Unbounded positive value where higher is better.
    /// </summary>
    public required double SparseScore { get; init; }

    /// <summary>
    /// The combined score after Reciprocal Rank Fusion (RRF) merges the dense
    /// and sparse rankings. This is the primary sort key for result ordering.
    /// Formula: RRF(d) = 1/(k + rank_dense) + 1/(k + rank_sparse), where k is typically 60.
    /// </summary>
    public required double FusedScore { get; init; }
}
