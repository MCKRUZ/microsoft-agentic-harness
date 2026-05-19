using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Infrastructure.AI.RAG.QueryTransform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Top-level RAG pipeline orchestrator that routes queries through classification,
/// retrieval, reranking, CRAG evaluation, and context assembly. This is the single
/// entry point consumed by agent tools and MediatR handlers.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline flow for vector-based strategies:
/// <list type="number">
///   <item>Classify query via <see cref="QueryRouter"/> (if classification enabled).</item>
///   <item>Retrieve via <see cref="IHybridRetriever"/>.</item>
///   <item>Rerank via <see cref="IReranker"/>.</item>
///   <item>Evaluate via <see cref="ICragEvaluator"/> — may trigger refinement loop.</item>
///   <item>Assemble via <see cref="IRagContextAssembler"/>.</item>
/// </list>
/// </para>
/// <para>
/// For <see cref="RetrievalStrategy.GraphRag"/>, the pipeline bypasses vector
/// retrieval and delegates directly to <see cref="IGraphRagService.GlobalSearchAsync"/>.
/// </para>
/// </remarks>
public sealed class RagOrchestrator : IRagOrchestrator
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Orchestration");

    private const int MaxCragRetries = 2;
    private const int DefaultMaxTokens = 4096;
    private const int DefaultTopK = 10;

    private readonly IHybridRetriever _hybridRetriever;
    private readonly IReranker _reranker;
    private readonly ICragEvaluator _cragEvaluator;
    private readonly IRagContextAssembler _contextAssembler;
    private readonly IGraphRagService _graphRagService;
    private readonly IFeedbackWeightedScorer? _feedbackScorer;
    private readonly QueryRouter _queryRouter;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<RagOrchestrator> _logger;
    private readonly IQueryComplexityClassifier? _complexityClassifier;
    private readonly IRetrievalDecisionGate? _decisionGate;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagOrchestrator"/> class.
    /// </summary>
    /// <param name="hybridRetriever">Hybrid dense+sparse retriever.</param>
    /// <param name="reranker">Cross-encoder or semantic reranker.</param>
    /// <param name="cragEvaluator">CRAG relevance evaluator.</param>
    /// <param name="contextAssembler">Final context assembly stage.</param>
    /// <param name="graphRagService">Knowledge graph-based retrieval service.</param>
    /// <param name="feedbackScorer">Optional feedback-weighted scorer. Null when feedback is disabled.</param>
    /// <param name="queryRouter">Query classification and transformation router.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    /// <param name="logger">Logger for recording pipeline decisions.</param>
    /// <param name="complexityClassifier">Optional complexity classifier for tiered routing. Null disables complexity routing.</param>
    /// <param name="decisionGate">Optional retrieval decision gate. Null disables complexity routing.</param>
    public RagOrchestrator(
        IHybridRetriever hybridRetriever,
        IReranker reranker,
        ICragEvaluator cragEvaluator,
        IRagContextAssembler contextAssembler,
        IGraphRagService graphRagService,
        IFeedbackWeightedScorer? feedbackScorer,
        QueryRouter queryRouter,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<RagOrchestrator> logger,
        IQueryComplexityClassifier? complexityClassifier = null,
        IRetrievalDecisionGate? decisionGate = null)
    {
        ArgumentNullException.ThrowIfNull(hybridRetriever);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(cragEvaluator);
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(graphRagService);
        ArgumentNullException.ThrowIfNull(queryRouter);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _hybridRetriever = hybridRetriever;
        _reranker = reranker;
        _cragEvaluator = cragEvaluator;
        _contextAssembler = contextAssembler;
        _graphRagService = graphRagService;
        _feedbackScorer = feedbackScorer;
        _queryRouter = queryRouter;
        _configMonitor = configMonitor;
        _logger = logger;
        _complexityClassifier = complexityClassifier;
        _decisionGate = decisionGate;
    }

    /// <inheritdoc />
    public async Task<RagAssembledContext> SearchAsync(
        string query,
        int? topK = null,
        string? collectionName = null,
        RetrievalStrategy? strategyOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.search");
        var sw = Stopwatch.StartNew();
        var ragConfig = _configMonitor.CurrentValue.AI.Rag;
        var effectiveTopK = topK ?? ragConfig.Retrieval.TopK;
        if (effectiveTopK <= 0) effectiveTopK = DefaultTopK;

        // Complexity-based routing (when enabled and services available)
        if (ragConfig.ComplexityRouting.Enabled
            && _complexityClassifier is not null
            && _decisionGate is not null
            && strategyOverride is null)
        {
            var complexity = await _complexityClassifier.ClassifyAsync(query, cancellationToken);
            var decision = complexity is not null ? _decisionGate.Decide(complexity, topK) : null;

            if (complexity is null || decision is null)
            {
                _logger.LogWarning(
                    "Complexity routing unavailable (classifier={ClassifierResult}, gate={GateResult}); falling back to full pipeline",
                    complexity is null ? "null" : "ok",
                    decision is null ? "null" : "ok");
            }
            else
            {
                activity?.SetTag("rag.query.complexity", decision.Complexity.ToString().ToLowerInvariant());
                activity?.SetTag("rag.query.skip_retrieval", decision.SkipRetrieval);
                activity?.SetTag("rag.query.complexity_confidence", complexity.Confidence);
                activity?.SetTag("rag.query.use_reranking", decision.UseReranking);
                activity?.SetTag("rag.query.use_crag", decision.UseCragEvaluation);
                activity?.SetTag("rag.query.effective_top_k", decision.TopK);

                if (decision.SkipRetrieval)
                {
                    _logger.LogInformation(
                        "Complexity routing: skipping retrieval for {Complexity} query",
                        decision.Complexity);
                    return new RagAssembledContext
                    {
                        AssembledText = string.Empty,
                        TotalTokens = 0,
                        WasTruncated = false,
                    };
                }

                return await ExecuteRoutedPipelineAsync(query, decision, collectionName, cancellationToken);
            }
        }

        // Step 1: Determine retrieval strategy
        var strategy = strategyOverride ?? await ClassifyStrategyAsync(query, cancellationToken);
        var strategyTag = strategy.ToString().ToLowerInvariant();
        activity?.SetTag(RagConventions.RetrievalStrategy, strategyTag);

        var tags = new KeyValuePair<string, object?>(RagConventions.RetrievalStrategy, strategyTag);
        RagRetrievalMetrics.Queries.Add(1, tags);

        _logger.LogInformation(
            "RAG orchestrator: Strategy={Strategy}, TopK={TopK}, MaxTokens={MaxTokens}",
            strategy, effectiveTopK, DefaultMaxTokens);

        try
        {
            // Step 2: Route by strategy
            if (strategy == RetrievalStrategy.GraphRag)
                return await ExecuteGraphRagAsync(query, cancellationToken);

            return await ExecuteVectorPipelineAsync(
                query, effectiveTopK, collectionName, cancellationToken);
        }
        finally
        {
            sw.Stop();
            RagRetrievalMetrics.RetrievalDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }

    private async Task<RetrievalStrategy> ClassifyStrategyAsync(
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var (classification, _) = await _queryRouter.RouteAsync(query, cancellationToken);
            return classification.Strategy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query classification failed; falling back to HybridVectorBm25");
            return RetrievalStrategy.HybridVectorBm25;
        }
    }

    private async Task<RagAssembledContext> ExecuteGraphRagAsync(
        string query, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.graph_pipeline");

        _logger.LogInformation("Routing to GraphRAG global search");
        return await _graphRagService.GlobalSearchAsync(query, communityLevel: 0, cancellationToken);
    }

    private async Task<RagAssembledContext> ExecuteVectorPipelineAsync(
        string query,
        int topK,
        string? collectionName,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.vector_pipeline");
        var currentQuery = query;
        var attempt = 0;

        while (attempt <= MaxCragRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Retrieve
            var candidates = await _hybridRetriever.RetrieveAsync(
                currentQuery, topK, collectionName, cancellationToken);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("Retrieval returned 0 candidates for query: {QueryLength} chars",
                    currentQuery.Length);
                return CreateEmptyContext("No relevant documents found.");
            }

            activity?.SetTag(RagConventions.RetrievalChunksReturned, candidates.Count);
            RagRetrievalMetrics.ChunksReturned.Record(candidates.Count);

            if (candidates.Count > 0)
                RagRetrievalMetrics.Hits.Add(1);

            // Rerank
            var reranked = await _reranker.RerankAsync(
                currentQuery, candidates, topK, cancellationToken);

            // Feedback blending (when enabled)
            if (_feedbackScorer is not null)
                reranked = await _feedbackScorer.BlendFeedbackAsync(
                    reranked, currentQuery, cancellationToken);

            // CRAG evaluation
            var evaluation = await _cragEvaluator.EvaluateAsync(
                currentQuery, candidates, cancellationToken);

            activity?.SetTag(RagConventions.CragAction, evaluation.Action.ToString().ToLowerInvariant());
            activity?.SetTag(RagConventions.CragScore, evaluation.RelevanceScore);

            switch (evaluation.Action)
            {
                case CorrectionAction.Accept:
                    _logger.LogInformation(
                        "CRAG accepted: score={Score:F2}, assembling {Count} results",
                        evaluation.RelevanceScore, reranked.Count);
                    return await _contextAssembler.AssembleAsync(
                        reranked, DefaultMaxTokens, cancellationToken);

                case CorrectionAction.Refine when attempt < MaxCragRetries:
                    attempt++;
                    currentQuery = $"{query} (refined: {evaluation.Reasoning ?? "improve relevance"})";
                    _logger.LogInformation(
                        "CRAG refine: score={Score:F2}, attempt {Attempt}/{MaxRetries}",
                        evaluation.RelevanceScore, attempt, MaxCragRetries);
                    continue;

                case CorrectionAction.Reject:
                    _logger.LogWarning(
                        "CRAG rejected: score={Score:F2}, reason={Reason}",
                        evaluation.RelevanceScore, evaluation.Reasoning);
                    return CreateEmptyContext(
                        evaluation.Reasoning ?? "Retrieved content not relevant to the query.");

                default:
                    // Refine exhausted or WebFallback — assemble what we have
                    _logger.LogInformation(
                        "CRAG {Action} after {Attempts} attempts; assembling best available",
                        evaluation.Action, attempt);
                    var filtered = FilterWeakChunks(reranked, evaluation.WeakChunkIds);
                    return await _contextAssembler.AssembleAsync(
                        filtered, DefaultMaxTokens, cancellationToken);
            }
        }

        return CreateEmptyContext("Query refinement exhausted without acceptable results.");
    }

    private async Task<RagAssembledContext> ExecuteRoutedPipelineAsync(
        string query,
        RetrievalDecision decision,
        string? collectionName,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.routed_pipeline");
        activity?.SetTag("rag.complexity", decision.Complexity.ToString().ToLowerInvariant());
        activity?.SetTag("rag.use_reranking", decision.UseReranking);
        activity?.SetTag("rag.use_crag", decision.UseCragEvaluation);

        // Step 1: Retrieve with decision's topK
        var candidates = await _hybridRetriever.RetrieveAsync(
            query, decision.TopK, collectionName, cancellationToken);

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "Routed pipeline: retrieval returned 0 candidates (complexity={Complexity})",
                decision.Complexity);
            return new RagAssembledContext
            {
                AssembledText = string.Empty,
                TotalTokens = 0,
                WasTruncated = false,
            };
        }

        IReadOnlyList<RerankedResult> reranked;

        // Step 2: Conditional reranking
        if (decision.UseReranking)
        {
            reranked = await _reranker.RerankAsync(
                query, candidates, decision.TopK, cancellationToken);
        }
        else
        {
            // Convert retrieval results directly to reranked results (preserve order, skip reranker)
            reranked = candidates.Select((r, i) => new RerankedResult
            {
                RetrievalResult = r,
                RerankScore = r.FusedScore,
                OriginalRank = i + 1,
                RerankRank = i + 1,
            }).ToList();
        }

        // Step 3: Conditional CRAG evaluation (reuses existing vector pipeline loop logic)
        if (decision.UseCragEvaluation)
        {
            return await ExecuteVectorPipelineAsync(
                query, decision.TopK, collectionName, cancellationToken);
        }

        // Step 4: Direct assembly (skip CRAG)
        _logger.LogInformation(
            "Routed pipeline: assembling {Count} results without CRAG (complexity={Complexity})",
            reranked.Count, decision.Complexity);
        return await _contextAssembler.AssembleAsync(reranked, DefaultMaxTokens, cancellationToken);
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

    private static RagAssembledContext CreateEmptyContext(string reasoning) => new()
    {
        AssembledText = reasoning,
        TotalTokens = 0,
        WasTruncated = false
    };
}
