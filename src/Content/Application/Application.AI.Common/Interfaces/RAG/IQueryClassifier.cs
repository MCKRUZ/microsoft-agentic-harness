using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Classifies incoming queries to determine the optimal retrieval strategy.
/// Uses LLM-based few-shot classification to identify query type (simple lookup,
/// comparison, multi-hop reasoning, thematic/broad) and map it to a retrieval
/// strategy. Runs before retrieval so the pipeline can adapt its approach to
/// each query rather than using a fixed strategy.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Use a lightweight, fast model (economy tier via <see cref="IRagModelRouter"/>)
///         since classification is on the critical path for every query.</item>
///   <item>Prompt design: Include 2-3 few-shot examples per query type. The prompt should
///         output structured JSON with type, strategy, confidence, and reasoning fields.</item>
///   <item>Cache classifications for identical queries to avoid redundant LLM calls.</item>
///   <item>When the LLM fails or confidence is below threshold, return a conservative
///         default (e.g., <c>HybridVectorBm25</c> with confidence 0.5).</item>
///   <item>Emit OpenTelemetry metrics: classification distribution by type, latency,
///         confidence histogram, and fallback rate.</item>
/// </list>
/// </remarks>
public interface IQueryClassifier
{
    /// <summary>
    /// Classifies a query to determine its type and optimal retrieval strategy.
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The classification result with type, strategy, and confidence.</returns>
    Task<QueryClassification> ClassifyAsync(
        string query,
        CancellationToken cancellationToken = default);
}
