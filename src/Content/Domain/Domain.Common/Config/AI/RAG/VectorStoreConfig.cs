namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the vector store provider including endpoint,
/// embedding model, and index settings.
/// Bound from <c>AppConfig:AI:Rag:VectorStore</c> in appsettings.json.
/// </summary>
public class VectorStoreConfig
{
    /// <summary>
    /// Gets or sets the vector store provider name: the key the default <c>IVectorStore</c> and <c>IBm25Store</c> are
    /// resolved by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped keys are <c>"faiss"</c> (an in-process vector index paired with an in-memory SQLite FTS5 table, both
    /// lost at restart) and <c>"azure_ai_search"</c> (one Azure AI Search index backing both stores). A consumer may
    /// register further keys.
    /// </para>
    /// <para>
    /// Defaults to <c>"faiss"</c>, which needs no endpoint, so a host that omits this section runs on a store it actually
    /// has (#1012). Choosing <c>"azure_ai_search"</c> requires <see cref="Endpoint"/>, and an <see cref="Endpoint"/>
    /// requires <c>"azure_ai_search"</c>: <c>RagConfigValidator</c> refuses either half alone at startup. Otherwise a
    /// host that lost its endpoint, or set one without choosing the provider, would erase a person from a store that holds
    /// nothing while their chunks stayed in the real index, and certify the erasure complete.
    /// </para>
    /// </remarks>
    public string Provider { get; set; } = "faiss";

    /// <summary>
    /// Gets or sets the vector store endpoint URL.
    /// For Azure AI Search, this is the service URL (e.g., <c>https://myservice.search.windows.net</c>). Required when,
    /// and only when, <see cref="Provider"/> is <c>"azure_ai_search"</c>.
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
