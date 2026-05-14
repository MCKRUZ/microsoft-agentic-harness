using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Application.Core.Workflows.Rag;

/// <summary>
/// Encapsulates the full vector retrieval pipeline: retrieve, rerank, optional feedback
/// blending, and CRAG evaluation with up to <see cref="MaxRefinementAttempts"/> refinement
/// retries. This executor handles the CRAG loop internally because MAF workflow graphs
/// are DAGs and cannot represent cycles as edges.
/// </summary>
/// <remarks>
/// <para>
/// The internal loop mirrors the logic in <c>RagOrchestrator.ExecuteVectorPipelineAsync</c>:
/// <list type="number">
///   <item>Retrieve via <see cref="IHybridRetriever"/>.</item>
///   <item>Rerank via <see cref="IReranker"/>.</item>
///   <item>Optionally blend feedback weights via <see cref="IFeedbackWeightedScorer"/>.</item>
///   <item>Evaluate via <see cref="ICragEvaluator"/>.</item>
///   <item>If <see cref="CorrectionAction.Refine"/> and attempts remain, refine the query and loop.</item>
///   <item>On <see cref="CorrectionAction.Accept"/>, <see cref="CorrectionAction.Reject"/>,
///         <see cref="CorrectionAction.WebFallback"/>, or exhausted retries, emit final results.</item>
/// </list>
/// </para>
/// <para>
/// The <see cref="IFeedbackWeightedScorer"/> dependency is optional. When null (feedback
/// disabled), the reranked results pass directly to CRAG evaluation without score blending.
/// </para>
/// </remarks>
public sealed class VectorRetrievalExecutor : Executor<ClassifiedQuery, VectorRetrievalOutput>
{
    private const int MaxRefinementAttempts = 2;

    private readonly IHybridRetriever _retriever;
    private readonly IReranker _reranker;
    private readonly ICragEvaluator _cragEvaluator;
    private readonly IFeedbackWeightedScorer? _feedbackScorer;
    private readonly ILogger<VectorRetrievalExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VectorRetrievalExecutor"/> class.
    /// </summary>
    /// <param name="retriever">Hybrid dense+sparse retriever.</param>
    /// <param name="reranker">Cross-encoder or semantic reranker.</param>
    /// <param name="cragEvaluator">CRAG relevance evaluator for retrieval quality gating.</param>
    /// <param name="feedbackScorer">
    /// Optional feedback-weighted scorer. Null when feedback blending is disabled.
    /// </param>
    /// <param name="logger">Logger for recording retrieval decisions and CRAG loop iterations.</param>
    public VectorRetrievalExecutor(
        IHybridRetriever retriever,
        IReranker reranker,
        ICragEvaluator cragEvaluator,
        IFeedbackWeightedScorer? feedbackScorer,
        ILogger<VectorRetrievalExecutor> logger)
        : base("vector_retrieval")
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(cragEvaluator);
        ArgumentNullException.ThrowIfNull(logger);

        _retriever = retriever;
        _reranker = reranker;
        _cragEvaluator = cragEvaluator;
        _feedbackScorer = feedbackScorer;
        _logger = logger;
    }

    /// <summary>
    /// Executes the retrieve-rerank-evaluate loop, refining the query up to
    /// <see cref="MaxRefinementAttempts"/> times when CRAG prescribes refinement.
    /// </summary>
    /// <param name="message">The classified query with strategy and retrieval parameters.</param>
    /// <param name="context">The workflow execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The vector retrieval output with ranked results and loop metadata.</returns>
    public override async ValueTask<VectorRetrievalOutput> HandleAsync(
        ClassifiedQuery message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var currentQuery = message.Query;
        var attempt = 0;

        while (attempt <= MaxRefinementAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var candidates = await _retriever.RetrieveAsync(
                currentQuery, message.TopK, message.CollectionName, cancellationToken);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("Retrieval returned 0 candidates on attempt {Attempt}", attempt);
                return new VectorRetrievalOutput(
                    message.Query, [], WasRefined: attempt > 1, AttemptCount: attempt);
            }

            var reranked = await _reranker.RerankAsync(
                currentQuery, candidates, message.TopK, cancellationToken);

            if (_feedbackScorer is not null)
            {
                reranked = await _feedbackScorer.BlendFeedbackAsync(
                    reranked, currentQuery, cancellationToken);
            }

            var evaluation = await _cragEvaluator.EvaluateAsync(
                currentQuery, candidates, cancellationToken);

            switch (evaluation.Action)
            {
                case CorrectionAction.Accept:
                    _logger.LogInformation(
                        "CRAG accepted on attempt {Attempt}: score={Score:F2}",
                        attempt, evaluation.RelevanceScore);
                    return new VectorRetrievalOutput(
                        message.Query, reranked, WasRefined: attempt > 1, AttemptCount: attempt);

                case CorrectionAction.Refine when attempt <= MaxRefinementAttempts:
                    currentQuery = $"{message.Query} (refined: {evaluation.Reasoning ?? "improve relevance"})";
                    _logger.LogInformation(
                        "CRAG refine on attempt {Attempt}/{Max}: score={Score:F2}",
                        attempt, MaxRefinementAttempts, evaluation.RelevanceScore);
                    continue;

                case CorrectionAction.Reject:
                    _logger.LogWarning(
                        "CRAG rejected on attempt {Attempt}: score={Score:F2}",
                        attempt, evaluation.RelevanceScore);
                    return new VectorRetrievalOutput(
                        message.Query, [], WasRefined: attempt > 1, AttemptCount: attempt);

                default:
                    _logger.LogInformation(
                        "CRAG {Action} after {Attempt} attempts; returning best available",
                        evaluation.Action, attempt);
                    var filtered = FilterWeakChunks(reranked, evaluation.WeakChunkIds);
                    return new VectorRetrievalOutput(
                        message.Query, filtered, WasRefined: attempt > 1, AttemptCount: attempt);
            }
        }

        return new VectorRetrievalOutput(
            message.Query, [], WasRefined: true, AttemptCount: attempt);
    }

    private static IReadOnlyList<RerankedResult> FilterWeakChunks(
        IReadOnlyList<RerankedResult> results,
        IReadOnlyList<string> weakChunkIds)
    {
        if (weakChunkIds.Count == 0) return results;

        var weakSet = weakChunkIds.ToHashSet();
        return results
            .Where(r => !weakSet.Contains(r.RetrievalResult.Chunk.Id))
            .ToList();
    }
}
