using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Combines dense (vector) and sparse (BM25) retrieval with Reciprocal Rank Fusion
/// (RRF) to produce a single ranked list. Runs both searches concurrently and merges
/// results using the formula: <c>score = 1/(k + rank_dense) + 1/(k + rank_sparse)</c>.
/// Falls back gracefully to single-source retrieval when one store is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// Configuration via <c>AppConfig:AI:Rag:Retrieval</c>:
/// <list type="bullet">
///   <item><c>RrfK</c> — the fusion constant (default 60). Higher values flatten
///         rank differences.</item>
///   <item><c>EnableHybrid</c> — when <c>false</c>, only dense retrieval is used.</item>
///   <item><c>TopK</c> — the number of candidates requested from each store
///         (2x the final topK for sufficient overlap).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class HybridRetriever : IHybridRetriever
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Retrieval");

    private readonly IVectorStore _vectorStore;
    private readonly IBm25Store _bm25Store;
    private readonly IEmbeddingService _embeddingService;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<HybridRetriever> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HybridRetriever"/> class.
    /// </summary>
    /// <param name="vectorStore">The dense vector store for semantic search.</param>
    /// <param name="bm25Store">The sparse BM25 store for keyword search.</param>
    /// <param name="embeddingService">The embedding service for query vectorization.</param>
    /// <param name="appConfig">The application configuration monitor.</param>
    /// <param name="logger">The logger instance.</param>
    public HybridRetriever(
        IVectorStore vectorStore,
        IBm25Store bm25Store,
        IEmbeddingService embeddingService,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<HybridRetriever> logger)
    {
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _embeddingService = embeddingService;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("HybridRetrieval");
        var config = _appConfig.CurrentValue.AI.Rag.Retrieval;

        activity?.SetTag("rag.retrieval.strategy", config.EnableHybrid ? "hybrid" : "dense_only");
        activity?.SetTag("rag.retrieval.top_k", topK);
        activity?.SetTag("rag.retrieval.rrf_k", config.RrfK);

        var candidateCount = topK * 3;

        var queryEmbedding = await _embeddingService.EmbedQueryAsync(query, cancellationToken);

        if (!config.EnableHybrid)
        {
            return await _vectorStore.SearchAsync(
                queryEmbedding, topK, collectionName, cancellationToken);
        }

        var denseResults = RunDenseSearchAsync(queryEmbedding, candidateCount, collectionName, cancellationToken);
        var sparseResults = RunSparseSearchAsync(query, candidateCount, collectionName, cancellationToken);

        await Task.WhenAll(denseResults, sparseResults);

        var dense = await denseResults;
        var sparse = await sparseResults;

        activity?.SetTag("rag.retrieval.dense_count", dense.Count);
        activity?.SetTag("rag.retrieval.sparse_count", sparse.Count);

        var fused = ApplyReciprocalRankFusion(dense, sparse, config.RrfK, topK);

        _logger.LogDebug(
            "Hybrid retrieval returned {Count} results (dense: {Dense}, sparse: {Sparse}, fused topK: {TopK})",
            fused.Count, dense.Count, sparse.Count, topK);

        activity?.SetTag("rag.retrieval.fused_count", fused.Count);

        return fused;
    }

    private async Task<IReadOnlyList<RetrievalResult>> RunDenseSearchAsync(
        ReadOnlyMemory<float> embedding,
        int candidateCount,
        string? collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _vectorStore.SearchAsync(
                embedding, candidateCount, collectionName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dense vector search failed, falling back to sparse-only");
            return [];
        }
    }

    private async Task<IReadOnlyList<RetrievalResult>> RunSparseSearchAsync(
        string query,
        int candidateCount,
        string? collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _bm25Store.SearchAsync(
                query, candidateCount, collectionName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sparse BM25 search failed, falling back to dense-only");
            return [];
        }
    }

    private static IReadOnlyList<RetrievalResult> ApplyReciprocalRankFusion(
        IReadOnlyList<RetrievalResult> denseResults,
        IReadOnlyList<RetrievalResult> sparseResults,
        double rrfK,
        int topK)
    {
        var chunkScores = new Dictionary<string, (DocumentChunk Chunk, double DenseScore, double SparseScore, double FusedScore)>();

        for (var rank = 0; rank < denseResults.Count; rank++)
        {
            var result = denseResults[rank];
            var rrfScore = 1.0 / (rrfK + rank + 1);

            chunkScores[result.Chunk.Id] = (result.Chunk, result.DenseScore, 0.0, rrfScore);
        }

        for (var rank = 0; rank < sparseResults.Count; rank++)
        {
            var result = sparseResults[rank];
            var rrfScore = 1.0 / (rrfK + rank + 1);

            if (chunkScores.TryGetValue(result.Chunk.Id, out var existing))
            {
                chunkScores[result.Chunk.Id] = (
                    existing.Chunk,
                    existing.DenseScore,
                    result.SparseScore,
                    existing.FusedScore + rrfScore);
            }
            else
            {
                chunkScores[result.Chunk.Id] = (result.Chunk, 0.0, result.SparseScore, rrfScore);
            }
        }

        return chunkScores.Values
            .OrderByDescending(s => s.FusedScore)
            .Take(topK)
            .Select(s => new RetrievalResult
            {
                Chunk = s.Chunk,
                DenseScore = s.DenseScore,
                SparseScore = s.SparseScore,
                FusedScore = s.FusedScore,
            })
            .ToList();
    }
}
