using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Semantic chunking strategy that splits text into sentences, computes embeddings
/// for candidate groups, and merges adjacent candidates with high cosine similarity.
/// More expensive than structure-aware or fixed-size chunking because it requires
/// embedding pre-computation, but produces higher-quality boundaries for unstructured
/// content (transcripts, emails, logs).
/// Registered as keyed service <c>"semantic"</c>.
/// </summary>
public sealed class SemanticChunker : IChunkingService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");
    private const double SimilarityThreshold = 0.8;

    private readonly IEmbeddingService _embeddingService;
    private readonly int _targetTokens;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticChunker"/> class.
    /// </summary>
    /// <param name="embeddingService">Embedding service for computing candidate similarities.</param>
    /// <param name="appConfig">Application configuration providing chunking parameters.</param>
    public SemanticChunker(
        IEmbeddingService embeddingService,
        IOptionsMonitor<AppConfig> appConfig)
    {
        _embeddingService = embeddingService;
        _targetTokens = appConfig.CurrentValue.AI.Rag.Ingestion.TargetTokens;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string content,
        SkeletonNode structure,
        Uri sourceUri,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.chunk.semantic");
        activity?.SetTag(RagConventions.ModelOperation, "chunk_semantic");

        var documentId = ComputeDocumentId(sourceUri);
        var sentences = SplitIntoSentences(content);

        if (sentences.Count == 0)
            return [];

        // Group sentences into candidate chunks of roughly target size
        var candidates = GroupIntoCandidates(sentences);

        if (candidates.Count <= 1)
        {
            return CreateSingleChunk(candidates, documentId, structure, sourceUri);
        }

        // Embed each candidate for similarity comparison
        var candidateChunks = candidates
            .Select((text, i) => new DocumentChunk
            {
                Id = $"candidate_{i}",
                DocumentId = documentId,
                SectionPath = "candidate",
                Content = text,
                Tokens = text.Length / 4,
                Metadata = new ChunkMetadata
                {
                    SourceUri = sourceUri,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            })
            .ToList();

        var embeddedCandidates = await _embeddingService.EmbedAsync(candidateChunks, cancellationToken);

        // Merge adjacent candidates with high similarity
        var mergedTexts = MergeBySimlarity(embeddedCandidates);

        // Build final chunks
        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;
        var charOffset = 0;

        foreach (var mergedText in mergedTexts)
        {
            var sectionPath = FindSectionPath(structure, charOffset);

            chunks.Add(new DocumentChunk
            {
                Id = $"{documentId}_chunk_{chunkIndex++}",
                DocumentId = documentId,
                SectionPath = sectionPath,
                Content = mergedText,
                Tokens = mergedText.Length / 4,
                Metadata = new ChunkMetadata
                {
                    SourceUri = sourceUri,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

            charOffset += mergedText.Length;
        }

        activity?.SetTag(RagConventions.IngestChunksProduced, chunks.Count);
        return chunks;
    }

    private static List<string> SplitIntoSentences(string content)
    {
        var sentences = new List<string>();
        var separators = new[] { ". ", ".\n", "\n\n" };
        var remaining = content.AsSpan();
        var start = 0;

        while (start < content.Length)
        {
            var minIndex = -1;
            var minSepLength = 0;

            foreach (var sep in separators)
            {
                var idx = content.IndexOf(sep, start, StringComparison.Ordinal);
                if (idx >= 0 && (minIndex < 0 || idx < minIndex))
                {
                    minIndex = idx;
                    minSepLength = sep.Length;
                }
            }

            if (minIndex < 0)
            {
                var tail = content[start..].Trim();
                if (tail.Length > 0) sentences.Add(tail);
                break;
            }

            var sentence = content[start..(minIndex + minSepLength)].Trim();
            if (sentence.Length > 0) sentences.Add(sentence);
            start = minIndex + minSepLength;
        }

        return sentences;
    }

    private List<string> GroupIntoCandidates(List<string> sentences)
    {
        var candidates = new List<string>();
        var buffer = "";
        var targetChars = _targetTokens * 4;

        foreach (var sentence in sentences)
        {
            if (buffer.Length + sentence.Length + 1 > targetChars && buffer.Length > 0)
            {
                candidates.Add(buffer);
                buffer = "";
            }

            buffer = buffer.Length > 0 ? buffer + " " + sentence : sentence;
        }

        if (buffer.Length > 0)
            candidates.Add(buffer);

        return candidates;
    }

    private static List<string> MergeBySimlarity(IReadOnlyList<DocumentChunk> embeddedCandidates)
    {
        var merged = new List<string>();
        var buffer = embeddedCandidates[0].Content;
        var prevEmbedding = embeddedCandidates[0].Embedding;

        for (var i = 1; i < embeddedCandidates.Count; i++)
        {
            var current = embeddedCandidates[i];
            var similarity = prevEmbedding is not null && current.Embedding is not null
                ? CosineSimilarity(prevEmbedding, current.Embedding)
                : 0.0;

            if (similarity >= SimilarityThreshold)
            {
                buffer += "\n\n" + current.Content;
            }
            else
            {
                merged.Add(buffer);
                buffer = current.Content;
            }

            prevEmbedding = current.Embedding;
        }

        merged.Add(buffer);
        return merged;
    }

    private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count) return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator < 1e-10 ? 0.0 : dot / denominator;
    }

    private static IReadOnlyList<DocumentChunk> CreateSingleChunk(
        List<string> candidates,
        string documentId,
        SkeletonNode structure,
        Uri sourceUri)
    {
        var text = string.Join("\n\n", candidates);
        if (string.IsNullOrWhiteSpace(text)) return [];

        return
        [
            new DocumentChunk
            {
                Id = $"{documentId}_chunk_0",
                DocumentId = documentId,
                SectionPath = FindSectionPath(structure, 0),
                Content = text,
                Tokens = text.Length / 4,
                Metadata = new ChunkMetadata
                {
                    SourceUri = sourceUri,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            }
        ];
    }

    private static string FindSectionPath(SkeletonNode root, int offset)
    {
        var best = root;
        SearchDeepest(root, offset, ref best);
        return best.GetBreadcrumb();
    }

    private static void SearchDeepest(SkeletonNode node, int offset, ref SkeletonNode best)
    {
        foreach (var child in node.Children)
        {
            if (offset >= child.StartOffset && offset < child.EndOffset)
            {
                best = child;
                SearchDeepest(child, offset, ref best);
                return;
            }
        }
    }

    private static string ComputeDocumentId(Uri sourceUri) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sourceUri.ToString())))[..16];
}
