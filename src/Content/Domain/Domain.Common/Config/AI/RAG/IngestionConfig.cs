namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for document ingestion and chunking.
/// Bound from <c>AppConfig:AI:Rag:Ingestion</c> in appsettings.json.
/// </summary>
public class IngestionConfig
{
    /// <summary>
    /// Gets or sets the default chunking strategy.
    /// Options: <c>"structure_aware"</c>, <c>"fixed_size"</c>, <c>"semantic"</c>.
    /// </summary>
    public string DefaultStrategy { get; set; } = "structure_aware";

    /// <summary>
    /// Gets or sets the target token count per chunk. Chunks are split to
    /// approximate this size while respecting structural boundaries.
    /// </summary>
    public int TargetTokens { get; set; } = 512;

    /// <summary>
    /// Gets or sets the number of overlap tokens between adjacent chunks
    /// to preserve cross-boundary context.
    /// </summary>
    public int OverlapTokens { get; set; } = 64;

    /// <summary>
    /// Gets or sets whether contextual enrichment is enabled. When true,
    /// each chunk is prepended with a short LLM-generated summary of its
    /// position within the parent document.
    /// </summary>
    public bool EnableContextualEnrichment { get; set; } = true;

    /// <summary>
    /// Gets or sets whether RAPTOR hierarchical summarization is enabled.
    /// When true, chunks are recursively clustered and summarized to create
    /// a multi-level retrieval tree.
    /// </summary>
    public bool EnableRaptorSummaries { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum depth of the RAPTOR summarization tree.
    /// Only applies when <see cref="EnableRaptorSummaries"/> is <c>true</c>.
    /// </summary>
    public int MaxRaptorDepth { get; set; } = 3;
}
