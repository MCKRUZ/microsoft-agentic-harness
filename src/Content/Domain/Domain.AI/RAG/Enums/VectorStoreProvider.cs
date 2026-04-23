namespace Domain.AI.RAG.Enums;

/// <summary>
/// Identifies the backing vector store used for embedding storage and similarity search.
/// Each provider has different trade-offs for cost, scalability, and feature set.
/// </summary>
public enum VectorStoreProvider
{
    /// <summary>
    /// Azure AI Search with vector index support. Managed service with hybrid search
    /// (vector + keyword), semantic ranking, and built-in security via Azure RBAC.
    /// Recommended for production workloads in Azure environments.
    /// </summary>
    AzureAISearch,

    /// <summary>
    /// Facebook AI Similarity Search — an in-process library for dense vector search.
    /// Zero infrastructure cost, excellent for development and small-to-medium corpora.
    /// Not suitable for distributed or multi-instance deployments.
    /// </summary>
    Faiss,

    /// <summary>
    /// A custom or third-party vector store implementation provided via the
    /// infrastructure layer's dependency injection. Use when the built-in
    /// providers don't meet specific requirements.
    /// </summary>
    Custom
}
