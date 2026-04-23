using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
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
    private readonly QueryRouter _queryRouter;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<RagOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagOrchestrator"/> class.
    /// </summary>
    /// <param name="hybridRetriever">Hybrid dense+sparse retriever.</param>
    /// <param name="reranker">Cross-encoder or semantic reranker.</param>
    /// <param name="cragEvaluator">CRAG relevance evaluator.</param>
    /// <param name="contextAssembler">Final context assembly stage.</param>
    /// <param name="graphRagService">Knowledge graph-based retrieval service.</param>
    /// <param name="queryRouter">Query classification and transformation router.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    /// <param name="logger">Logger for recording pipeline decisions.</param>
    public RagOrchestrator(
        IHybridRetriever hybridRetriever,
        IReranker reranker,
        ICragEvaluator cragEvaluator,
        IRagContextAssembler contextAssembler,
        IGraphRagService graphRagService,
        QueryRouter queryRouter,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<RagOrchestrator> logger)
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
        _queryRouter = queryRouter;
        _configMonitor = configMonitor;
        _logger = logger;
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
        var ragConfig = _configMonitor.CurrentValue.AI.Rag;
        var effectiveTopK = topK ?? ragConfig.Retrieval.TopK;
        if (effectiveTopK <= 0) effectiveTopK = DefaultTopK;

        // Step 1: Determine retrieval strategy
        var strategy = strategyOverride ?? await ClassifyStrategyAsync(query, cancellationToken);
        activity?.SetTag(RagConventions.RetrievalStrategy, strategy.ToString().ToLowerInvariant());

        _logger.LogInformation(
            "RAG orchestrator: Strategy={Strategy}, TopK={TopK}, MaxTokens={MaxTokens}",
            strategy, effectiveTopK, DefaultMaxTokens);

        // Step 2: Route by strategy
        if (strategy == RetrievalStrategy.GraphRag)
            return await ExecuteGraphRagAsync(query, cancellationToken);

        return await ExecuteVectorPipelineAsync(
            query, effectiveTopK, collectionName, cancellationToken);
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

            // Rerank
            var reranked = await _reranker.RerankAsync(
                currentQuery, candidates, topK, cancellationToken);

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
