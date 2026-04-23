using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// In-memory vector store for local development that uses brute-force cosine
/// similarity search. Named after FAISS but does not depend on the native FAISS
/// library -- a real FAISS binding is deferred to a future phase. Registered as
/// keyed service <c>"faiss"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Storage is partitioned by collection name in a nested
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Thread-safe for concurrent
/// indexing and searching. Suitable for small corpora (&lt;100k chunks) during
/// development and testing.
/// </para>
/// <para>
/// Cosine similarity is computed using SIMD-friendly <see cref="Vector{T}"/>
/// operations via <see cref="CosineSimilarity"/> for ~4x throughput on AVX2
/// hardware compared to scalar loops.
/// </para>
/// </remarks>
public sealed class FaissVectorStore : IVectorStore
{
    private const string DefaultCollection = "default";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (DocumentChunk Chunk, float[] Embedding)>>
        _collections = new();

    private readonly ILogger<FaissVectorStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FaissVectorStore"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public FaissVectorStore(ILogger<FaissVectorStore> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task IndexAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var collection = GetOrCreateCollection(collectionName);

        foreach (var chunk in chunks)
        {
            if (chunk.Embedding is not { Count: > 0 }) continue;

            var embedding = chunk.Embedding.ToArray();
            collection[chunk.Id] = (chunk, embedding);
        }

        _logger.LogDebug(
            "Indexed {Count} chunks into in-memory vector store (collection: {Collection})",
            chunks.Count, collectionName ?? DefaultCollection);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var collection = GetOrCreateCollection(collectionName);
        var querySpan = queryEmbedding.Span;

        var scored = new List<(DocumentChunk Chunk, double Score)>(collection.Count);

        foreach (var (_, (chunk, embedding)) in collection)
        {
            var score = CosineSimilarity(querySpan, embedding.AsSpan());
            scored.Add((chunk, score));
        }

        var results = scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select(s => new RetrievalResult
            {
                Chunk = s.Chunk,
                DenseScore = s.Score,
                SparseScore = 0.0,
                FusedScore = s.Score,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RetrievalResult>>(results);
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        string documentId,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var collection = GetOrCreateCollection(collectionName);
        var keysToRemove = collection
            .Where(kvp => kvp.Value.Chunk.DocumentId == documentId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            collection.TryRemove(key, out _);
        }

        _logger.LogDebug(
            "Deleted {Count} chunks for document {DocumentId} from in-memory store",
            keysToRemove.Count, documentId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes cosine similarity between two vectors using SIMD-friendly operations.
    /// Returns a value in [-1, 1] where 1 indicates identical direction.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cosine similarity score.</returns>
    internal static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;

        float dot = 0f, normA = 0f, normB = 0f;

        var simdLength = Vector<float>.Count;
        var i = 0;

        var aFloats = MemoryMarshal.Cast<float, float>(a);
        var bFloats = MemoryMarshal.Cast<float, float>(b);

        for (; i <= a.Length - simdLength; i += simdLength)
        {
            var va = new Vector<float>(aFloats.Slice(i, simdLength));
            var vb = new Vector<float>(bFloats.Slice(i, simdLength));

            dot += Vector.Dot(va, vb);
            normA += Vector.Dot(va, va);
            normB += Vector.Dot(vb, vb);
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0f ? 0.0 : dot / denominator;
    }

    private ConcurrentDictionary<string, (DocumentChunk Chunk, float[] Embedding)> GetOrCreateCollection(
        string? collectionName)
    {
        var key = collectionName ?? DefaultCollection;
        return _collections.GetOrAdd(key, _ => new());
    }
}
