using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Fixed-size chunking strategy that splits content using a simple sliding window
/// of <c>TargetTokens * 4</c> characters with <c>OverlapTokens * 4</c> character overlap.
/// No structure awareness — serves as a fast fallback for unstructured content where
/// heading-based splitting is unreliable. Still populates <see cref="DocumentChunk.SectionPath"/>
/// using the closest ancestor in the skeleton tree.
/// Registered as keyed service <c>"fixed_size"</c>.
/// </summary>
public sealed class FixedSizeChunker : IChunkingService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");

    private readonly int _targetChars;
    private readonly int _overlapChars;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedSizeChunker"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration providing chunking parameters.</param>
    public FixedSizeChunker(IOptionsMonitor<AppConfig> appConfig)
    {
        var ingestion = appConfig.CurrentValue.AI.Rag.Ingestion;
        _targetChars = ingestion.TargetTokens * 4;
        _overlapChars = ingestion.OverlapTokens * 4;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string content,
        SkeletonNode structure,
        Uri sourceUri,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.chunk.fixed_size");
        activity?.SetTag(RagConventions.ModelOperation, "chunk_fixed_size");

        var documentId = ComputeDocumentId(sourceUri);
        var chunks = new List<DocumentChunk>();
        var offset = 0;
        var chunkIndex = 0;

        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(_targetChars, content.Length - offset);
            var chunkText = content.Substring(offset, length);
            var sectionPath = FindSectionPath(structure, offset);

            chunks.Add(new DocumentChunk
            {
                Id = $"{documentId}_chunk_{chunkIndex++}",
                DocumentId = documentId,
                SectionPath = sectionPath,
                Content = chunkText,
                Tokens = chunkText.Length / 4,
                Metadata = new ChunkMetadata
                {
                    SourceUri = sourceUri,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

            // Advance by window minus overlap; ensure forward progress
            var step = Math.Max(1, _targetChars - _overlapChars);
            offset += step;
        }

        activity?.SetTag(RagConventions.IngestChunksProduced, chunks.Count);
        return Task.FromResult<IReadOnlyList<DocumentChunk>>(chunks);
    }

    /// <summary>
    /// Finds the deepest skeleton node whose range contains the given character offset
    /// and returns its breadcrumb path. Falls back to "Document Root" when no heading
    /// covers the offset.
    /// </summary>
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
