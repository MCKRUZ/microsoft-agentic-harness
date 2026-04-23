using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;

namespace Infrastructure.AI.RAG.Assembly;

/// <summary>
/// Thread-safe accumulator for citation spans during RAG context assembly.
/// Records the mapping between regions of the assembled text and their source
/// chunks, enabling inline citations and "click to verify" source attribution.
/// Registered as transient so each assembly operation gets a fresh instance.
/// </summary>
public sealed class CitationTracker : ICitationTracker
{
    private readonly ConcurrentBag<CitationSpan> _spans = [];

    /// <inheritdoc />
    public void Track(DocumentChunk chunk, int assembledOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (assembledOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(assembledOffset), "Offset must be non-negative.");

        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");

        _spans.Add(new CitationSpan
        {
            ChunkId = chunk.Id,
            DocumentUri = chunk.Metadata.SourceUri,
            SectionPath = chunk.SectionPath,
            StartOffset = assembledOffset,
            EndOffset = assembledOffset + length
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<CitationSpan> GetCitations()
    {
        return _spans
            .OrderBy(s => s.StartOffset)
            .ThenBy(s => s.ChunkId, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _spans.Clear();
    }
}
