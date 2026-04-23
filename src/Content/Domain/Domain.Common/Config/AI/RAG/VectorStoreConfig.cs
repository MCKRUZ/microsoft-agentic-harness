namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the vector store provider including endpoint,
/// embedding model, and index settings.
/// Bound from <c>AppConfig:AI:Rag:VectorStore</c> in appsettings.json.
/// </summary>
public class VectorStoreConfig
{
    /// <summary>
    /// Gets or sets the vector store provider name.
    /// Options: <c>"azure_ai_search"</c>, <c>"qdrant"</c>, <c>"chroma"</c>.
    /// </summary>
    public string Provider { get; set; } = "azure_ai_search";

    /// <summary>
    /// Gets or sets the vector store endpoint URL.
    /// For Azure AI Search, this is the service URL (e.g., <c>https://myservice.search.windows.net</c>).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the index name within the vector store.
    /// </summary>
    public string IndexName { get; set; } = "rag-chunks";

    /// <summary>
    /// Gets or sets the embedding model deployment name used to generate
    /// vector representations of chunks and queries.
    /// </summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";

    /// <summary>
    /// Gets or sets the embedding vector dimensions. Must match the
    /// dimensionality of the configured <see cref="EmbeddingModel"/>.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 3072;

    /// <summary>
    /// Gets or sets the API key for the vector store. Should be stored
    /// in User Secrets (dev) or Azure Key Vault (prod), never in appsettings.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets a value indicating whether the vector store is configured
    /// with a valid endpoint.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
