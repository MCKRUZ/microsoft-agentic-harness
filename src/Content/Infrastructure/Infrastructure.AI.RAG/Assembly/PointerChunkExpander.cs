using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Assembly;

/// <summary>
/// Expands retrieved chunks by including sibling chunks that share the same parent
/// section (simplified Proxy-Pointer RAG pattern). When a chunk has a
/// <see cref="ChunkMetadata.ParentSectionId"/>, its siblings are included to provide
/// broader section-level context. Deduplicates by tracking seen chunk IDs so shared
/// parents produce only one copy of each sibling.
/// </summary>
/// <remarks>
/// This is a simplified implementation that works with the sibling metadata already
/// present on each chunk. A full implementation would query a persistent skeleton
/// store to load parent sections by ID and retrieve the complete section text.
/// </remarks>
public sealed class PointerChunkExpander : IPointerExpander
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Assembly");

    private readonly ILogger<PointerChunkExpander> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PointerChunkExpander"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording expansion outcomes.</param>
    public PointerChunkExpander(ILogger<PointerChunkExpander> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentChunk>> ExpandAsync(
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.assembly.pointer_expansion");
        activity?.SetTag("rag.assembly.input_chunks", chunks.Count);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var expanded = new List<DocumentChunk>(chunks.Count);

        // Build a lookup of all chunks by ID for sibling resolution
        var chunkById = new Dictionary<string, DocumentChunk>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            chunkById.TryAdd(chunk.Id, chunk);
        }

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(chunk.Id))
                continue;

            expanded.Add(chunk);

            // If this chunk has a parent section, include its siblings
            if (string.IsNullOrEmpty(chunk.Metadata.ParentSectionId))
                continue;

            foreach (var siblingId in chunk.Metadata.SiblingChunkIds)
            {
                if (!seen.Add(siblingId))
                    continue;

                // Only include siblings that are already in our retrieved set
                if (chunkById.TryGetValue(siblingId, out var sibling))
                {
                    expanded.Add(sibling);
                }
            }
        }

        activity?.SetTag("rag.assembly.expanded_chunks", expanded.Count);

        var addedCount = expanded.Count - chunks.Count;
        if (addedCount > 0)
        {
            _logger.LogInformation(
                "Pointer expansion added {AddedCount} sibling chunks ({InputCount} -> {OutputCount})",
                addedCount, chunks.Count, expanded.Count);
        }
        else
        {
            _logger.LogDebug(
                "Pointer expansion: no siblings added ({Count} chunks unchanged)",
                chunks.Count);
        }

        return Task.FromResult<IReadOnlyList<DocumentChunk>>(expanded);
    }
}
