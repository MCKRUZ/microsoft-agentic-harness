using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Generates contextual prefixes for document chunks using an economy-tier LLM
/// (Anthropic Contextual Retrieval pattern). For each chunk, sends the full document
/// and chunk content to the LLM to produce a 2-3 sentence summary situating the
/// chunk within the broader document. Processes chunks in parallel batches of 10.
/// Enrichment failures are logged but do not fail the batch — chunks without
/// enrichment retain a <c>null</c> <see cref="ChunkMetadata.ContextualPrefix"/>.
/// </summary>
public sealed class ContextualChunkEnricher : IContextualEnricher
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");
    private const int BatchSize = 10;
    private const int MaxDocumentChars = 12_000;

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<ContextualChunkEnricher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextualChunkEnricher"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting economy-tier chat client.</param>
    /// <param name="logger">Logger for recording enrichment progress and failures.</param>
    public ContextualChunkEnricher(
        IRagModelRouter modelRouter,
        ILogger<ContextualChunkEnricher> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentChunk>> EnrichAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string fullDocumentContent,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.contextual_enrichment");

        var tier = _modelRouter.GetTierForOperation("contextual_enrichment");
        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "contextual_enrichment");

        var chatClient = _modelRouter.GetClientForOperation("contextual_enrichment");
        var truncatedDoc = TruncateDocument(fullDocumentContent);
        var enrichedChunks = new DocumentChunk[chunks.Count];

        // Process in batches for parallelism
        for (var batchStart = 0; batchStart < chunks.Count; batchStart += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BatchSize, chunks.Count);
            var tasks = new List<Task>();

            for (var i = batchStart; i < batchEnd; i++)
            {
                var index = i;
                var chunk = chunks[index];
                tasks.Add(EnrichSingleChunkAsync(chatClient, truncatedDoc, chunk, enrichedChunks, index, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        var enrichedCount = enrichedChunks.Count(c => c.Metadata.ContextualPrefix is not null);
        activity?.SetTag("rag.ingest.enriched_count", enrichedCount);

        _logger.LogInformation(
            "Contextual enrichment complete: {Enriched}/{Total} chunks enriched",
            enrichedCount, chunks.Count);

        return enrichedChunks;
    }

    private async Task EnrichSingleChunkAsync(
        IChatClient chatClient,
        string truncatedDoc,
        DocumentChunk chunk,
        DocumentChunk[] results,
        int index,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = $"""
                <document>{truncatedDoc}</document>
                <chunk>{chunk.Content}</chunk>
                Provide a concise 2-3 sentence context for this chunk within the document. Focus on what section it belongs to and what it covers.
                """;

            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var contextualPrefix = response.Text?.Trim();

            results[index] = chunk with
            {
                Metadata = chunk.Metadata with { ContextualPrefix = contextualPrefix }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Contextual enrichment failed for chunk {ChunkId}; preserving without prefix",
                chunk.Id);

            results[index] = chunk;
        }
    }

    private static string TruncateDocument(string document) =>
        document.Length > MaxDocumentChars
            ? document[..MaxDocumentChars] + "\n\n[...truncated...]"
            : document;
}
