using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Input to the RAG pipeline workflow. Encapsulates the user query and retrieval
/// parameters needed to drive the full classify-retrieve-rerank-evaluate-assemble pipeline.
/// </summary>
/// <param name="Query">The user's search query.</param>
/// <param name="TopK">Maximum number of results to include in the assembled context.</param>
/// <param name="CollectionName">Optional vector store collection/index. Null uses the default.</param>
/// <param name="StrategyOverride">
/// When set, bypasses the query classifier and forces this retrieval strategy.
/// Useful for testing or specialized workflows that know the optimal strategy upfront.
/// </param>
public sealed record RagWorkflowInput(
    string Query,
    int TopK = 10,
    string? CollectionName = null,
    string? StrategyOverride = null);

/// <summary>
/// Output of the strategy classification stage. Carries the original query parameters
/// alongside the classified <see cref="Strategy"/> so downstream executors can route
/// to the correct retrieval path (vector vs graph).
/// </summary>
/// <param name="Query">The original user query.</param>
/// <param name="TopK">Maximum number of results to retrieve.</param>
/// <param name="CollectionName">Optional collection/index name.</param>
/// <param name="Strategy">The retrieval strategy selected by the classifier.</param>
public sealed record ClassifiedQuery(
    string Query,
    int TopK,
    string? CollectionName,
    RetrievalStrategy Strategy);

/// <summary>
/// Output of the vector retrieval executor after completing the retrieve-rerank-CRAG
/// evaluation loop. Contains the final reranked results and metadata about whether
/// the CRAG loop triggered query refinement.
/// </summary>
/// <param name="OriginalQuery">The query that initiated retrieval (may differ from the user query if refined).</param>
/// <param name="RankedResults">The reranked and optionally feedback-blended retrieval results.</param>
/// <param name="WasRefined">Whether the CRAG evaluator triggered at least one refinement pass.</param>
/// <param name="AttemptCount">Total number of retrieve-rerank-evaluate cycles executed (1 = no refinement).</param>
public sealed record VectorRetrievalOutput(
    string OriginalQuery,
    IReadOnlyList<RerankedResult> RankedResults,
    bool WasRefined,
    int AttemptCount);
