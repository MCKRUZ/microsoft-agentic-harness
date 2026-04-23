using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// RAPTOR (Recursive Abstractive Processing for Tree-Organized Retrieval) summarizer.
/// Clusters chunks into sequential groups of 5-10, summarizes each cluster with an
/// economy-tier LLM, and recurses until <paramref name="maxDepth"/> is reached or
/// a single cluster remains. Uses sequential grouping (not k-means) as a template
/// simplification — production deployments should upgrade to GMM clustering on embeddings.
/// </summary>
public sealed class RaptorSummarizer : IRaptorSummarizer
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");
    private const int MinClusterSize = 5;
    private const int MaxClusterSize = 10;

    private readonly IRagModelRouter _modelRouter;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<RaptorSummarizer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RaptorSummarizer"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting economy-tier chat client.</param>
    /// <param name="embeddingService">Embedding service for embedding generated summaries.</param>
    /// <param name="logger">Logger for recording summarization progress.</param>
    public RaptorSummarizer(
        IRagModelRouter modelRouter,
        IEmbeddingService embeddingService,
        ILogger<RaptorSummarizer> logger)
    {
        _modelRouter = modelRouter;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentChunk>> SummarizeAsync(
        IReadOnlyList<DocumentChunk> chunks,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.raptor_summarization");
        activity?.SetTag(RagConventions.ModelOperation, "raptor_summarization");

        if (maxDepth <= 0 || chunks.Count <= 1)
            return chunks;

        var allChunks = new List<DocumentChunk>(chunks);
        var currentLevel = chunks;

        for (var depth = 1; depth <= maxDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clusters = GroupIntoClusters(currentLevel);

            if (clusters.Count <= 1 && depth > 1)
            {
                _logger.LogDebug("RAPTOR stopping at depth {Depth}: single cluster", depth);
                break;
            }

            _logger.LogInformation(
                "RAPTOR level {Level}: {ClusterCount} clusters from {ChunkCount} chunks",
                depth, clusters.Count, currentLevel.Count);

            var summaryChunks = await SummarizeClustersAsync(clusters, depth, cancellationToken);
            var embeddedSummaries = await _embeddingService.EmbedAsync(summaryChunks, cancellationToken);

            allChunks.AddRange(embeddedSummaries);
            currentLevel = embeddedSummaries;

            if (currentLevel.Count <= 1)
                break;
        }

        activity?.SetTag(RagConventions.IngestChunksProduced, allChunks.Count);
        return allChunks;
    }

    private static List<List<DocumentChunk>> GroupIntoClusters(IReadOnlyList<DocumentChunk> chunks)
    {
        var clusters = new List<List<DocumentChunk>>();
        var clusterSize = Math.Max(MinClusterSize, Math.Min(MaxClusterSize, chunks.Count / 3));

        for (var i = 0; i < chunks.Count; i += clusterSize)
        {
            var end = Math.Min(i + clusterSize, chunks.Count);
            clusters.Add(chunks.Skip(i).Take(end - i).ToList());
        }

        return clusters;
    }

    private async Task<IReadOnlyList<DocumentChunk>> SummarizeClustersAsync(
        List<List<DocumentChunk>> clusters,
        int level,
        CancellationToken cancellationToken)
    {
        var chatClient = _modelRouter.GetClientForOperation("raptor_summarization");
        var summaries = new List<DocumentChunk>();

        for (var i = 0; i < clusters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cluster = clusters[i];
            var concatenated = string.Join("\n\n---\n\n", cluster.Select(c => c.Content));

            var prompt = $"""
                Summarize the following document chunks into a concise, comprehensive summary.
                Preserve key facts, entities, and relationships. The summary should be useful
                for answering questions about the content.

                {concatenated}
                """;

            try
            {
                var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
                var summaryText = response.Text?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(summaryText))
                    continue;

                var documentId = cluster[0].DocumentId;
                var sectionPath = $"RAPTOR/Level-{level}/Cluster-{i}";

                summaries.Add(new DocumentChunk
                {
                    Id = $"{documentId}_raptor_L{level}_C{i}",
                    DocumentId = documentId,
                    SectionPath = sectionPath,
                    Content = summaryText,
                    Tokens = summaryText.Length / 4,
                    Metadata = new ChunkMetadata
                    {
                        SourceUri = cluster[0].Metadata.SourceUri,
                        CreatedAt = DateTimeOffset.UtcNow,
                        ParentSectionId = null
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RAPTOR summarization failed for level {Level} cluster {Cluster}",
                    level, i);
            }
        }

        return summaries;
    }
}
