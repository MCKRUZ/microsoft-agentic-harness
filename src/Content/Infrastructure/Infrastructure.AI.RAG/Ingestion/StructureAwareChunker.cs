using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Ingestion;

/// <summary>
/// Structure-aware chunking strategy that splits documents at heading boundaries.
/// Walks the <see cref="SkeletonNode"/> tree, collecting text within each section.
/// Sections exceeding <see cref="Domain.Common.Config.AI.RAG.IngestionConfig.TargetTokens"/>
/// are split at paragraph boundaries. Each chunk is prefixed with a breadcrumb path
/// (e.g., <c>[Section: H1 &gt; H2 &gt; H3]</c>) for retrieval context.
/// Registered as keyed service <c>"structure_aware"</c>.
/// </summary>
public sealed class StructureAwareChunker : IChunkingService
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.Ingestion");

    private readonly int _targetTokens;
    private readonly int _overlapTokens;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructureAwareChunker"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration providing chunking parameters.</param>
    public StructureAwareChunker(IOptionsMonitor<AppConfig> appConfig)
    {
        var ingestion = appConfig.CurrentValue.AI.Rag.Ingestion;
        _targetTokens = ingestion.TargetTokens;
        _overlapTokens = ingestion.OverlapTokens;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string content,
        SkeletonNode structure,
        Uri sourceUri,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.ingest.chunk.structure_aware");
        activity?.SetTag(RagConventions.ModelOperation, "chunk_structure_aware");

        var documentId = ComputeDocumentId(sourceUri);
        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        CollectChunks(content, structure, sourceUri, documentId, chunks, ref chunkIndex, previousOverlap: null);

        activity?.SetTag(RagConventions.IngestChunksProduced, chunks.Count);
        return Task.FromResult<IReadOnlyList<DocumentChunk>>(chunks);
    }

    private void CollectChunks(
        string content,
        SkeletonNode node,
        Uri sourceUri,
        string documentId,
        List<DocumentChunk> chunks,
        ref int chunkIndex,
        string? previousOverlap)
    {
        // For leaf nodes (no children), chunk the section text directly
        if (node.Children.Count == 0 && node.Level > 0)
        {
            var sectionText = content[node.StartOffset..node.EndOffset].Trim();
            if (string.IsNullOrWhiteSpace(sectionText)) return;

            var breadcrumb = node.GetBreadcrumb();
            SplitSection(sectionText, breadcrumb, sourceUri, documentId, chunks, ref chunkIndex, ref previousOverlap);
            return;
        }

        // For nodes with children, chunk any text between the heading and the first child
        if (node.Level > 0 && node.Children.Count > 0)
        {
            var firstChildStart = node.Children[0].StartOffset;
            if (firstChildStart > node.StartOffset)
            {
                var preamble = content[node.StartOffset..firstChildStart].Trim();
                if (!string.IsNullOrWhiteSpace(preamble))
                {
                    var breadcrumb = node.GetBreadcrumb();
                    SplitSection(preamble, breadcrumb, sourceUri, documentId, chunks, ref chunkIndex, ref previousOverlap);
                }
            }
        }

        // Recurse into children
        foreach (var child in node.Children)
        {
            CollectChunks(content, child, sourceUri, documentId, chunks, ref chunkIndex, previousOverlap);
        }
    }

    private void SplitSection(
        string sectionText,
        string breadcrumb,
        Uri sourceUri,
        string documentId,
        List<DocumentChunk> chunks,
        ref int chunkIndex,
        ref string? previousOverlap)
    {
        var targetChars = _targetTokens * 4;
        var overlapChars = _overlapTokens * 4;
        var prefix = $"[Section: {breadcrumb}]\n\n";

        if (EstimateTokens(sectionText) <= _targetTokens)
        {
            var chunkContent = prefix + (previousOverlap != null ? previousOverlap + "\n\n" : "") + sectionText;
            chunks.Add(CreateChunk(chunkContent, documentId, breadcrumb, sourceUri, ref chunkIndex));
            previousOverlap = sectionText.Length > overlapChars
                ? sectionText[^overlapChars..]
                : sectionText;
            return;
        }

        // Split at paragraph boundaries
        var paragraphs = sectionText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var buffer = previousOverlap ?? "";

        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (buffer.Length + trimmed.Length + 2 > targetChars && buffer.Length > 0)
            {
                var chunkContent = prefix + buffer;
                chunks.Add(CreateChunk(chunkContent, documentId, breadcrumb, sourceUri, ref chunkIndex));
                previousOverlap = buffer.Length > overlapChars ? buffer[^overlapChars..] : buffer;
                buffer = previousOverlap;
            }

            buffer = buffer.Length > 0 ? buffer + "\n\n" + trimmed : trimmed;
        }

        if (buffer.Length > 0)
        {
            var chunkContent = prefix + buffer;
            chunks.Add(CreateChunk(chunkContent, documentId, breadcrumb, sourceUri, ref chunkIndex));
            previousOverlap = buffer.Length > overlapChars ? buffer[^overlapChars..] : buffer;
        }
    }

    private static DocumentChunk CreateChunk(
        string content,
        string documentId,
        string sectionPath,
        Uri sourceUri,
        ref int chunkIndex)
    {
        var id = $"{documentId}_chunk_{chunkIndex++}";
        return new DocumentChunk
        {
            Id = id,
            DocumentId = documentId,
            SectionPath = sectionPath,
            Content = content,
            Tokens = EstimateTokens(content),
            Metadata = new ChunkMetadata
            {
                SourceUri = sourceUri,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
    }

    private static int EstimateTokens(string text) => text.Length / 4;

    private static string ComputeDocumentId(Uri sourceUri) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sourceUri.ToString())))[..16];
}
