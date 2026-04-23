using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Generates vector embeddings for document chunks and queries using
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from Microsoft.Extensions.AI.
/// Batches chunks in groups of 100, applies a 3-attempt exponential backoff retry
/// on transient failures, and prepends <see cref="ChunkMetadata.ContextualPrefix"/>
/// to chunk content before embedding when contextual enrichment has been applied.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");
    private const int BatchSize = 100;
    private const int MaxRetries = 3;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingService"/> class.
    /// </summary>
    /// <param name="embeddingGenerator">
    /// The embedding generator from Microsoft.Extensions.AI, configured with the
    /// appropriate embedding model (e.g., text-embedding-3-small).
    /// </param>
    /// <param name="logger">Logger for recording batch progress and failures.</param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentChunk>> EmbedAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.embed_chunks");
        activity?.SetTag(RagConventions.ModelOperation, "embed_chunks");

        var results = new List<DocumentChunk>();
        var totalTokens = 0;

        for (var batchStart = 0; batchStart < chunks.Count; batchStart += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BatchSize, chunks.Count);
            var batch = chunks.Skip(batchStart).Take(batchEnd - batchStart).ToList();

            var texts = batch.Select(PrepareEmbeddingText).ToList();
            totalTokens += texts.Sum(t => t.Length / 4);

            try
            {
                var embeddings = await GenerateWithRetryAsync(texts, cancellationToken);

                for (var i = 0; i < batch.Count && i < embeddings.Count; i++)
                {
                    var embeddingVector = embeddings[i].Vector.ToArray();
                    results.Add(batch[i] with { Embedding = embeddingVector });
                }

                _logger.LogDebug(
                    "Embedded batch {BatchStart}-{BatchEnd} ({Count} chunks)",
                    batchStart, batchEnd, batch.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Embedding batch {BatchStart}-{BatchEnd} failed after retries; skipping {Count} chunks",
                    batchStart, batchEnd, batch.Count);
            }
        }

        activity?.SetTag(RagConventions.IngestTokensEmbedded, totalTokens);
        activity?.SetTag(RagConventions.IngestChunksProduced, results.Count);

        _logger.LogInformation(
            "Embedding complete: {Embedded}/{Total} chunks embedded, ~{Tokens} tokens",
            results.Count, chunks.Count, totalTokens);

        return results;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.embed_query");
        activity?.SetTag(RagConventions.ModelOperation, "embed_query");

        var embeddings = await GenerateWithRetryAsync([query], cancellationToken);

        if (embeddings.Count == 0)
            throw new InvalidOperationException("Embedding generation returned no results for query.");

        return embeddings[0].Vector;
    }

    /// <summary>
    /// Prepares text for embedding by prepending the contextual prefix when present.
    /// </summary>
    private static string PrepareEmbeddingText(DocumentChunk chunk) =>
        chunk.Metadata.ContextualPrefix is { Length: > 0 } prefix
            ? $"{prefix}\n\n{chunk.Content}"
            : chunk.Content;

    /// <summary>
    /// Generates embeddings with exponential backoff retry (3 attempts, 1s/2s/4s delays).
    /// </summary>
    private async Task<IReadOnlyList<Embedding<float>>> GenerateWithRetryAsync(
        IList<string> texts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var result = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken);
                return result.ToList();
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex,
                    "Embedding attempt {Attempt}/{MaxRetries} failed; retrying in {Delay}s",
                    attempt + 1, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        // Final attempt — let it throw
        var final = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken);
        return final.ToList();
    }
}
