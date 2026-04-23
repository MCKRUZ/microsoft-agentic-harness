using Domain.AI.RAG.Models;

namespace Application.Core.CQRS.RAG.SearchDocuments;

/// <summary>
/// Result of a document search operation including reranked results,
/// timing information, and the retrieval strategy used.
/// </summary>
public record SearchDocumentsResult
{
    /// <summary>The reranked results ordered by descending relevance score.</summary>
    public required IReadOnlyList<RerankedResult> Results { get; init; }

    /// <summary>The retrieval strategy that was applied (e.g., "hybrid_vector_bm25").</summary>
    public required string Strategy { get; init; }

    /// <summary>Total wall-clock duration of the retrieval + reranking pipeline.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Number of candidate chunks returned by the retriever before reranking.</summary>
    public required int TotalCandidates { get; init; }

    /// <summary>Whether the search completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is <c>false</c>.</summary>
    public string? Error { get; init; }

    /// <summary>Fully assembled RAG context with pointer-expanded sections and citations.</summary>
    public string? AssembledContext { get; init; }
}
