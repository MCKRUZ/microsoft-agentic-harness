using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Top-level RAG pipeline orchestrator. Routes queries through classification,
/// transformation, retrieval, reranking, CRAG evaluation, and context assembly.
/// This is the single entry point consumed by agent tools (e.g., <c>DocumentSearchTool</c>)
/// and MediatR command handlers — callers should not compose pipeline stages manually.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Pipeline flow: Classify → Transform → Retrieve → Rerank → CRAG Evaluate
///         → Expand Pointers → Assemble Context. Each stage is optional and controlled
///         by the query classification and configuration.</item>
///   <item>Use <see cref="IQueryClassifier"/> to determine the retrieval strategy, then
///         dispatch to the appropriate retriever (<see cref="IHybridRetriever"/>,
///         <see cref="IVectorStore"/>-only, or <see cref="IGraphRagService"/>).</item>
///   <item>The <paramref name="strategyOverride"/> parameter bypasses classification,
///         allowing callers to force a specific strategy for testing or specialized workflows.</item>
///   <item>Wrap the entire pipeline in an OpenTelemetry activity span with stage-level
///         child spans for end-to-end latency visibility.</item>
///   <item>Apply circuit breakers on external service calls (embedding, LLM, vector store)
///         to degrade gracefully under load.</item>
/// </list>
/// </remarks>
public interface IRagOrchestrator
{
    /// <summary>
    /// Executes the full RAG pipeline for the given query and returns assembled context.
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="topK">
    /// Maximum number of final results to include in the assembled context.
    /// When null, uses the default from <c>AppConfig:AI:Rag:DefaultTopK</c>.
    /// </param>
    /// <param name="collectionName">
    /// Optional collection/index to search. Null uses the default collection.
    /// </param>
    /// <param name="strategyOverride">
    /// When specified, bypasses <see cref="IQueryClassifier"/> and uses this strategy directly.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled context with citations, ready for LLM prompt injection.</returns>
    Task<RagAssembledContext> SearchAsync(
        string query,
        int? topK = null,
        string? collectionName = null,
        RetrievalStrategy? strategyOverride = null,
        CancellationToken cancellationToken = default);
}
