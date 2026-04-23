namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for query transformation techniques including RAG-Fusion,
/// HyDE (Hypothetical Document Embeddings), and automatic query classification.
/// Bound from <c>AppConfig:AI:Rag:QueryTransform</c> in appsettings.json.
/// </summary>
public class QueryTransformConfig
{
    /// <summary>
    /// Gets or sets whether RAG-Fusion is enabled. When <c>true</c>, the
    /// original query is expanded into multiple variant queries whose results
    /// are fused via Reciprocal Rank Fusion (RRF).
    /// </summary>
    public bool EnableRagFusion { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of query variants to generate for RAG-Fusion.
    /// Only applies when <see cref="EnableRagFusion"/> is <c>true</c>.
    /// </summary>
    public int FusionVariantCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether HyDE (Hypothetical Document Embeddings) is enabled.
    /// When <c>true</c>, the LLM generates a hypothetical answer which is then
    /// embedded and used for retrieval instead of the raw query.
    /// </summary>
    public bool EnableHyde { get; set; } = false;

    /// <summary>
    /// Gets or sets whether automatic query classification is enabled.
    /// When <c>true</c>, incoming queries are classified (e.g., simple_lookup,
    /// multi_hop, global_thematic) to select the optimal retrieval strategy.
    /// </summary>
    public bool EnableClassification { get; set; } = true;
}
