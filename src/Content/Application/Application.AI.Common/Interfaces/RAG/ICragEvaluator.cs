using Domain.AI.RAG.Models;

namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// Evaluates retrieval quality using the Corrective RAG (CRAG) pattern.
/// Scores each retrieved chunk for relevance to the query and determines a
/// correction action: proceed with retrieved chunks, augment with web search,
/// or discard and re-query. Runs after reranking and before context assembly.
/// </summary>
/// <remarks>
/// <para><strong>Implementation guidance:</strong></para>
/// <list type="bullet">
///   <item>Use a lightweight LLM (economy tier) to score each (query, chunk) pair for
///         relevance on a 0-1 scale. A cross-encoder is an alternative to LLM-based scoring.</item>
///   <item>Aggregate per-chunk scores into an overall relevance score. Apply thresholds
///         from <c>AppConfig:AI:Rag:Crag</c> to determine the correction action:
///         <c>&gt; 0.7</c> → Proceed, <c>0.4-0.7</c> → Refine (keep good chunks, drop weak),
///         <c>&lt; 0.4</c> → WebSearch fallback or Refuse.</item>
///   <item>The <see cref="CragEvaluation.WeakChunkIds"/> list should include IDs of chunks
///         scoring below the per-chunk threshold so the assembler can exclude them.</item>
///   <item>CRAG evaluation is optional — the orchestrator may skip it for simple lookups
///         where the query classifier indicates high confidence.</item>
///   <item>Emit metrics: evaluation latency, action distribution, average relevance.</item>
/// </list>
/// </remarks>
public interface ICragEvaluator
{
    /// <summary>
    /// Evaluates retrieval results for relevance and determines the correction action.
    /// </summary>
    /// <param name="query">The original user query.</param>
    /// <param name="results">The retrieval results to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result with relevance scores and recommended action.</returns>
    Task<CragEvaluation> EvaluateAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        CancellationToken cancellationToken = default);
}
