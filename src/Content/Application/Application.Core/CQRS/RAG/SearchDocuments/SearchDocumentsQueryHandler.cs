using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.RAG.SearchDocuments;

/// <summary>
/// Delegates to <see cref="IRagOrchestrator"/> for the full RAG pipeline
/// (classify, transform, retrieve, rerank, evaluate, expand, assemble).
/// </summary>
public sealed class SearchDocumentsQueryHandler
    : IRequestHandler<SearchDocumentsQuery, SearchDocumentsResult>
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Retrieval");

    private readonly IRagOrchestrator _orchestrator;
    private readonly ILogger<SearchDocumentsQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="SearchDocumentsQueryHandler"/> class.</summary>
    public SearchDocumentsQueryHandler(
        IRagOrchestrator orchestrator,
        ILogger<SearchDocumentsQueryHandler> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SearchDocumentsResult> Handle(
        SearchDocumentsQuery request,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("rag.retrieval.pipeline");
        activity?.SetTag(RagConventions.ModelOperation, "retrieval");
        var pipelineSw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "RAG search started via orchestrator: TopK={TopK}",
                request.TopK);

            var context = await _orchestrator.SearchAsync(
                request.Query, request.TopK, request.CollectionName,
                request.StrategyOverride, cancellationToken);

            pipelineSw.Stop();

            var results = context.Citations.Select((c, i) => new RerankedResult
            {
                RetrievalResult = new RetrievalResult
                {
                    Chunk = new DocumentChunk
                    {
                        Id = c.ChunkId,
                        DocumentId = c.DocumentUri?.ToString() ?? string.Empty,
                        Content = string.Empty,
                        SectionPath = c.SectionPath,
                        Tokens = 0,
                        Metadata = new ChunkMetadata
                        {
                            SourceUri = c.DocumentUri ?? new Uri("search://unknown"),
                            CreatedAt = DateTimeOffset.UtcNow,
                        },
                    },
                    DenseScore = 0,
                    SparseScore = 0,
                    FusedScore = 0,
                },
                RerankScore = 0,
                OriginalRank = i + 1,
                RerankRank = i + 1,
            }).ToList();

            RagRetrievalMetrics.ChunksReturned.Record(results.Count);

            _logger.LogInformation(
                "RAG search completed: {ResultCount} citations, {TotalTokens} tokens, {TotalMs}ms",
                results.Count, context.TotalTokens, pipelineSw.ElapsedMilliseconds);

            return new SearchDocumentsResult
            {
                Results = results,
                Strategy = RagConventions.StrategyValues.HybridVectorBm25,
                Duration = pipelineSw.Elapsed,
                TotalCandidates = results.Count,
                Success = true,
                AssembledContext = context.AssembledText,
            };
        }
        catch (Exception ex)
        {
            pipelineSw.Stop();
            _logger.LogError(ex, "RAG search failed for query: {QueryLength} chars", request.Query.Length);
            RagRetrievalMetrics.Errors.Add(1);

            return new SearchDocumentsResult
            {
                Results = [],
                Strategy = RagConventions.StrategyValues.HybridVectorBm25,
                Duration = pipelineSw.Elapsed,
                TotalCandidates = 0,
                Success = false,
                Error = "Document search failed. Check server logs for details.",
            };
        }
    }
}
