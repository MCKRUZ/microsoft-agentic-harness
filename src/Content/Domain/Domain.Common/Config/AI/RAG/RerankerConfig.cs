namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for post-retrieval reranking to improve chunk relevance
/// ordering. Bound from <c>AppConfig:AI:Rag:Reranker</c> in appsettings.json.
/// </summary>
public class RerankerConfig
{
    /// <summary>
    /// Gets or sets the reranking strategy.
    /// Options: <c>"azure_semantic"</c> (Azure AI Search semantic ranker),
    /// <c>"cross_encoder"</c> (cross-encoder model), <c>"none"</c> (skip reranking).
    /// </summary>
    public string Strategy { get; set; } = "none";

    /// <summary>
    /// Gets or sets the reranker model name or deployment. Only required
    /// when <see cref="Strategy"/> is <c>"cross_encoder"</c>.
    /// </summary>
    public string? ModelName { get; set; }
}
