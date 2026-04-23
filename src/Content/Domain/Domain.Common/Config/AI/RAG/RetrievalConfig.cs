namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the retrieval pipeline controlling result counts,
/// reciprocal rank fusion, and hybrid search.
/// Bound from <c>AppConfig:AI:Rag:Retrieval</c> in appsettings.json.
/// </summary>
public class RetrievalConfig
{
    /// <summary>
    /// Gets or sets the number of chunks to retrieve from the vector store
    /// before reranking.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of chunks to retain after reranking.
    /// Must be less than or equal to <see cref="TopK"/>.
    /// </summary>
    public int RerankTopK { get; set; } = 5;

    /// <summary>
    /// Gets or sets the RRF (Reciprocal Rank Fusion) constant <c>k</c>.
    /// Higher values reduce the impact of rank position differences when
    /// fusing results from multiple retrieval strategies.
    /// </summary>
    public double RrfK { get; set; } = 60.0;

    /// <summary>
    /// Gets or sets whether hybrid retrieval (vector + BM25) is enabled.
    /// When <c>false</c>, only dense vector search is used.
    /// </summary>
    public bool EnableHybrid { get; set; } = true;
}
