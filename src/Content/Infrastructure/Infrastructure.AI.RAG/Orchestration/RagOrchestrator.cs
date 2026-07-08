using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Retrieval;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
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
///   <item>Retrieve via <see cref="IHybridRetriever"/> or <see cref="IIterativeRetriever"/> (complex queries).</item>
///   <item>Rerank via <see cref="IReranker"/>.</item>
///   <item>Evaluate via <see cref="ICragEvaluator"/> — may trigger refinement loop.</item>
///   <item>Evaluate via <see cref="IAnswerFaithfulnessEvaluator"/> — may trigger CRAG fallback (complex queries).</item>
///   <item>Assemble via <see cref="IRagContextAssembler"/>.</item>
/// </list>
/// </para>
/// <para>
/// For <see cref="RetrievalStrategy.GraphRag"/>, the pipeline bypasses vector
/// retrieval and delegates directly to <see cref="IGraphRagService.GlobalSearchAsync"/>.
/// </para>
/// <para>
/// For <see cref="Domain.AI.Routing.Enums.TaskComplexity.Complex"/> queries (Phase B), the pipeline uses
/// multi-hop iterative retrieval with post-assembly faithfulness evaluation.
/// </para>
/// </remarks>
public sealed partial class RagOrchestrator : IRagOrchestrator
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
    private readonly IMultiSourceOrchestrator? _multiSourceOrchestrator;
    private readonly ITaskComplexityClassifier? _complexityClassifier;
    private readonly IRetrievalCostTracker? _costTracker;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<RagOrchestrator> _logger;
    private readonly IRetrievalDecisionGate? _decisionGate;
    private readonly IIterativeRetriever? _iterativeRetriever;
    private readonly IAnswerFaithfulnessEvaluator? _faithfulnessEvaluator;
    private readonly IRetrievalSource? _webSearchSource;

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
    /// <param name="multiSourceOrchestrator">Optional multi-source retrieval orchestrator. Null disables multi-source routing.</param>
    /// <param name="complexityClassifier">Optional task complexity classifier for tiered routing. Null disables complexity routing.</param>
    /// <param name="costTracker">Optional retrieval cost tracker. Null disables cost tracking.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    /// <param name="logger">Logger for recording pipeline decisions.</param>
    /// <param name="decisionGate">Optional retrieval decision gate. Null disables complexity routing.</param>
    /// <param name="iterativeRetriever">Optional multi-hop iterative retriever for complex queries. Null disables multi-hop.</param>
    /// <param name="faithfulnessEvaluator">Optional post-assembly faithfulness evaluator. Null disables faithfulness checks.</param>
    /// <param name="webSearchSource">Optional web-search retrieval source (keyed "web_search"). Invoked only when
    /// CRAG returns <see cref="CorrectionAction.WebFallback"/> and web search is enabled in
    /// <see cref="Domain.Common.Config.AI.RAG.MultiSourceConfig.EnabledSources"/>. Null disables web fallback.</param>
    public RagOrchestrator(
        IHybridRetriever hybridRetriever,
        IReranker reranker,
        ICragEvaluator cragEvaluator,
        IRagContextAssembler contextAssembler,
        IGraphRagService graphRagService,
        IFeedbackWeightedScorer? feedbackScorer,
        QueryRouter queryRouter,
        IMultiSourceOrchestrator? multiSourceOrchestrator,
        ITaskComplexityClassifier? complexityClassifier,
        IRetrievalCostTracker? costTracker,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<RagOrchestrator> logger,
        IRetrievalDecisionGate? decisionGate = null,
        IIterativeRetriever? iterativeRetriever = null,
        IAnswerFaithfulnessEvaluator? faithfulnessEvaluator = null,
        IRetrievalSource? webSearchSource = null)
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
        _multiSourceOrchestrator = multiSourceOrchestrator;
        _complexityClassifier = complexityClassifier;
        _costTracker = costTracker;
        _configMonitor = configMonitor;
        _logger = logger;
        _decisionGate = decisionGate;
        _iterativeRetriever = iterativeRetriever;
        _faithfulnessEvaluator = faithfulnessEvaluator;
        _webSearchSource = webSearchSource;
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
        if (_configMonitor.CurrentValue.AI.ModelRouting.Enabled
            && _complexityClassifier is not null
            && _decisionGate is not null
            && strategyOverride is null)
        {
            var complexity = await _complexityClassifier.ClassifyAsync(
                new AgentTurnContext { ConversationId = "rag-pipeline", UserMessage = query, TurnNumber = 1 },
                cancellationToken);
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

        // Step 1: Determine retrieval strategy (and any RAG-Fusion query variants).
        RetrievalStrategy strategy;
        IReadOnlyList<string> queryVariants = [query];
        if (strategyOverride is not null)
        {
            strategy = strategyOverride.Value;
        }
        else
        {
            (strategy, queryVariants) = await ClassifyStrategyAsync(query, cancellationToken);
        }

        var strategyTag = strategy.ToString().ToLowerInvariant();
        activity?.SetTag(RagConventions.RetrievalStrategy, strategyTag);

        var tags = new KeyValuePair<string, object?>(RagConventions.RetrievalStrategy, strategyTag);
        RagRetrievalMetrics.Queries.Add(1, tags);

        _logger.LogInformation(
            "RAG orchestrator: Strategy={Strategy}, TopK={TopK}, MaxTokens={MaxTokens}",
            strategy, effectiveTopK, DefaultMaxTokens);

        try
        {
            RagAssembledContext result;

            // Step 2: Route by strategy
            if (strategy == RetrievalStrategy.GraphRag)
            {
                result = await ExecuteGraphRagAsync(query, cancellationToken);
            }
            else if (ragConfig.MultiSource.Enabled
                     && _multiSourceOrchestrator is not null
                     && _complexityClassifier is not null)
            {
                result = await ExecuteMultiSourcePipelineAsync(
                    query, effectiveTopK, cancellationToken);
            }
            else
            {
                result = await ExecuteVectorPipelineAsync(
                    query, effectiveTopK, collectionName, queryVariants, cancellationToken);
            }

            _costTracker?.RecordCall(result.TotalTokens, 0, sw.Elapsed);
            return result;
        }
        finally
        {
            sw.Stop();
            RagRetrievalMetrics.RetrievalDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }

    private async Task<(RetrievalStrategy Strategy, IReadOnlyList<string> Variants)> ClassifyStrategyAsync(
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var (classification, variants) = await _queryRouter.RouteAsync(query, cancellationToken);
            return (classification.Strategy, variants);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query classification failed; falling back to HybridVectorBm25");
            return (RetrievalStrategy.HybridVectorBm25, [query]);
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
        IReadOnlyList<string> queryVariants,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.vector_pipeline");
        var currentQuery = query;
        var attempt = 0;

        while (attempt <= MaxCragRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Retrieve — fan out across RAG-Fusion variants on the first (unrefined) pass;
            // refined passes re-query the single refined string.
            var candidates = currentQuery == query
                ? await RetrieveWithFanOutAsync(
                    currentQuery, queryVariants, topK, collectionName, cancellationToken)
                : await _hybridRetriever.RetrieveAsync(
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

                case CorrectionAction.WebFallback when IsWebFallbackEnabled():
                {
                    _logger.LogInformation(
                        "CRAG web fallback: score={Score:F2}, augmenting with web search results",
                        evaluation.RelevanceScore);
                    var augmented = await MergeAndRerankWebResultsAsync(
                        currentQuery, reranked, topK, cancellationToken);
                    return await _contextAssembler.AssembleAsync(
                        augmented, DefaultMaxTokens, cancellationToken);
                }

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

        // Phase B: Complex tier uses multi-hop iterative retrieval
        var ragConfig = _configMonitor.CurrentValue.AI.Rag;
        if (decision.Complexity == TaskComplexity.Complex
            && ragConfig.MultiHop.Enabled
            && _iterativeRetriever is not null)
        {
            return await ExecuteMultiHopPipelineAsync(
                query, decision.TopK, collectionName, cancellationToken);
        }

        // Step 1: CRAG evaluation path (reuses existing vector pipeline loop with refinement).
        // The complexity-routed path does not carry RAG-Fusion variants, so retrieval runs on
        // the single query.
        if (decision.UseCragEvaluation)
        {
            return await ExecuteVectorPipelineAsync(
                query, decision.TopK, collectionName, [query], cancellationToken);
        }

        // Step 2: Retrieve with decision's topK
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

        // Step 3: Conditional reranking
        if (decision.UseReranking)
        {
            reranked = await _reranker.RerankAsync(
                query, candidates, decision.TopK, cancellationToken);
        }
        else
        {
            reranked = candidates.Select((r, i) => new RerankedResult
            {
                RetrievalResult = r,
                RerankScore = r.FusedScore,
                OriginalRank = i + 1,
                RerankRank = i + 1,
            }).ToList();
        }

        // Step 4: Direct assembly (skip CRAG)
        _logger.LogInformation(
            "Routed pipeline: assembling {Count} results without CRAG (complexity={Complexity})",
            reranked.Count, decision.Complexity);
        return await _contextAssembler.AssembleAsync(reranked, DefaultMaxTokens, cancellationToken);
    }

    private async Task<RagAssembledContext> ExecuteMultiSourcePipelineAsync(
        string query, int topK, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.orchestrator.multi_source_pipeline");
        var classification = await _complexityClassifier!.ClassifyAsync(
            new AgentTurnContext { ConversationId = "rag-pipeline", UserMessage = query, TurnNumber = 1 },
            cancellationToken);

        _logger.LogInformation(
            "Multi-source pipeline: Complexity={Complexity}, Confidence={Confidence:F2}",
            classification!.Complexity, classification.Confidence);

        var candidates = await _multiSourceOrchestrator!.RetrieveFromAllSourcesAsync(
            query, topK, classification.Complexity, cancellationToken);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("Multi-source retrieval returned 0 candidates");
            return CreateEmptyContext("No relevant documents found across any source.");
        }

        activity?.SetTag(RagConventions.RetrievalChunksReturned, candidates.Count);
        RagRetrievalMetrics.ChunksReturned.Record(candidates.Count);
        RagRetrievalMetrics.Hits.Add(1);

        var reranked = await _reranker.RerankAsync(query, candidates, topK, cancellationToken);

        if (_feedbackScorer is not null)
            reranked = await _feedbackScorer.BlendFeedbackAsync(reranked, query, cancellationToken);

        var evaluation = await _cragEvaluator.EvaluateAsync(query, candidates, cancellationToken);

        activity?.SetTag(RagConventions.CragAction, evaluation.Action.ToString().ToLowerInvariant());
        activity?.SetTag(RagConventions.CragScore, evaluation.RelevanceScore);

        if (evaluation.Action == CorrectionAction.Reject)
        {
            return CreateEmptyContext(
                evaluation.Reasoning ?? "Multi-source content not relevant to the query.");
        }

        if (evaluation.Action == CorrectionAction.Refine)
        {
            var filtered = FilterWeakChunks(reranked, evaluation.WeakChunkIds);
            return await _contextAssembler.AssembleAsync(filtered, DefaultMaxTokens, cancellationToken);
        }

        return await _contextAssembler.AssembleAsync(reranked, DefaultMaxTokens, cancellationToken);
    }

    /// <summary>
    /// Retrieves candidates for the query. When RAG-Fusion fan-out is enabled and the router
    /// produced more than one variant, retrieves for each (bounded) variant concurrently and fuses
    /// the per-variant result sets with the shared <see cref="ReciprocalRankFusion"/> primitive.
    /// Otherwise falls back to a single retrieval on the primary query (legacy behavior).
    /// </summary>
    private async Task<IReadOnlyList<RetrievalResult>> RetrieveWithFanOutAsync(
        string primaryQuery,
        IReadOnlyList<string> queryVariants,
        int topK,
        string? collectionName,
        CancellationToken cancellationToken)
    {
        var ragConfig = _configMonitor.CurrentValue.AI.Rag;

        if (!ragConfig.QueryTransform.EnableFusionRetrieval || queryVariants.Count <= 1)
        {
            return await _hybridRetriever.RetrieveAsync(
                primaryQuery, topK, collectionName, cancellationToken);
        }

        var cap = Math.Max(1, ragConfig.QueryTransform.FusionVariantCount + 1);
        var boundedVariants = queryVariants.Take(cap).ToList();

        var perVariantResults = await Task.WhenAll(boundedVariants.Select(v =>
            _hybridRetriever.RetrieveAsync(v, topK, collectionName, cancellationToken)));

        var rrfK = ragConfig.Retrieval.RrfK > 0 ? ragConfig.Retrieval.RrfK : ReciprocalRankFusion.DefaultK;
        var fused = ReciprocalRankFusion.Fuse(perVariantResults, static r => r.Chunk.Id, rrfK, topK);

        _logger.LogInformation(
            "RAG-Fusion fan-out: retrieved across {VariantCount} variants, fused to {FusedCount} results",
            boundedVariants.Count, fused.Count);

        return fused.Select(f => f.Item with { FusedScore = f.Score }).ToList();
    }

    /// <summary>
    /// Determines whether a CRAG <see cref="CorrectionAction.WebFallback"/> should actually invoke
    /// web search: the web source must be registered and "web_search" must be listed in
    /// <see cref="Domain.Common.Config.AI.RAG.MultiSourceConfig.EnabledSources"/>. When either is
    /// absent, the pipeline degrades gracefully to assembling the best local results.
    /// </summary>
    private bool IsWebFallbackEnabled()
    {
        if (_webSearchSource is null) return false;

        var enabledSources = _configMonitor.CurrentValue.AI.Rag.MultiSource.EnabledSources;
        return enabledSources.Contains("web_search", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Invokes the web-search retrieval source, merges its results with the existing reranked local
    /// set, and re-ranks the combined candidate pool through the configured <see cref="IReranker"/> so
    /// local and web hits are scored on a single comparable scale before assembly. Returns the original
    /// set unchanged when web search yields nothing.
    /// </summary>
    /// <remarks>
    /// The <see cref="IRagContextAssembler"/> orders purely by <see cref="RerankedResult.RerankScore"/>.
    /// Web results arrive carrying a <see cref="RetrievalResult.FusedScore"/> on a foreign scale (the web
    /// source's own scoring), so copying that raw value into <c>RerankScore</c> and concatenating it —
    /// as the previous implementation did — let web hits dominate the context or sink to the bottom
    /// regardless of true relevance, and risked blowing the token budget. Re-ranking the union puts every
    /// hit through the same cross-encoder / semantic scorer, which is the single comparable scale the
    /// assembler needs to make web and local compete fairly.
    /// </remarks>
    private async Task<IReadOnlyList<RerankedResult>> MergeAndRerankWebResultsAsync(
        string query,
        IReadOnlyList<RerankedResult> existing,
        int topK,
        CancellationToken cancellationToken)
    {
        var webResult = await _webSearchSource!.RetrieveAsync(
            query, topK, TaskComplexity.Moderate, cancellationToken);

        if (webResult.Results.Count == 0)
            return existing;

        var merged = existing
            .Select(r => r.RetrievalResult)
            .Concat(webResult.Results)
            .ToList();

        // Re-rank the full union so both local and web hits are scored on one comparable scale.
        // topK = merged.Count keeps every candidate (the assembler enforces the token budget).
        return await _reranker.RerankAsync(query, merged, merged.Count, cancellationToken);
    }

    private static RagAssembledContext CreateEmptyContext(string reasoning) => new()
    {
        AssembledText = reasoning,
        TotalTokens = 0,
        WasTruncated = false
    };
}
