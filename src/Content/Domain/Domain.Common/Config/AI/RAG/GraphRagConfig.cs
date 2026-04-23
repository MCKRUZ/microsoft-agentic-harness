namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for GraphRAG graph-based retrieval using community
/// summaries and entity relationships.
/// Bound from <c>AppConfig:AI:Rag:GraphRag</c> in appsettings.json.
/// </summary>
public class GraphRagConfig
{
    /// <summary>
    /// Gets or sets whether GraphRAG is enabled. When <c>false</c>,
    /// graph-based retrieval is skipped entirely.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the graph storage provider.
    /// Options: <c>"managed_code"</c> (in-memory), <c>"neo4j"</c>, <c>"cosmos_gremlin"</c>.
    /// </summary>
    public string GraphProvider { get; set; } = "managed_code";

    /// <summary>
    /// Gets or sets the connection string for the graph database.
    /// Only required when <see cref="GraphProvider"/> is not <c>"managed_code"</c>.
    /// Should be stored in User Secrets (dev) or Azure Key Vault (prod).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the community detection level used for retrieval.
    /// Level 0 is the most granular (individual entities); higher levels
    /// represent progressively broader community summaries.
    /// </summary>
    public int CommunityLevel { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum token budget for entity extraction
    /// prompts during graph construction.
    /// </summary>
    public int MaxEntityExtractionTokens { get; set; } = 4096;
}
