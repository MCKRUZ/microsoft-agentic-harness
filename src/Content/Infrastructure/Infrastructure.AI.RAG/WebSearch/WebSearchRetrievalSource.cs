using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;

namespace Infrastructure.AI.RAG.WebSearch;

/// <summary>
/// Adapts <see cref="IWebSearchProvider"/> to <see cref="IRetrievalSource"/>.
/// Converts web search results to <see cref="RetrievalResult"/> with rank-decay scoring.
/// Registered as keyed DI with key "web_search".
/// </summary>
internal sealed class WebSearchRetrievalSource(IWebSearchProvider webSearchProvider) : IRetrievalSource
{
    private const double DecayFactor = 0.85;

    /// <inheritdoc />
    public string SourceName => "web_search";

    /// <inheritdoc />
    public async Task<SourceRetrievalResult> RetrieveAsync(
        string query, int topK, QueryComplexity complexity, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var webResults = await webSearchProvider.SearchAsync(query, topK, cancellationToken);
        sw.Stop();

        var retrievalResults = webResults
            .Select((wr, index) => ToRetrievalResult(wr, index))
            .ToList();

        return new SourceRetrievalResult
        {
            SourceName = SourceName,
            Results = retrievalResults,
            Latency = sw.Elapsed,
            TokensUsed = 0
        };
    }

    private static RetrievalResult ToRetrievalResult(WebSearchResult wr, int rankIndex)
    {
        var score = Math.Pow(DecayFactor, rankIndex);
        var content = wr.Content ?? wr.Snippet;

        return new RetrievalResult
        {
            Chunk = new DocumentChunk
            {
                Id = $"web-{wr.Url.GetHashCode():x8}",
                DocumentId = wr.Url,
                SectionPath = wr.Title,
                Content = content,
                Tokens = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                Metadata = new ChunkMetadata
                {
                    SourceUri = Uri.TryCreate(wr.Url, UriKind.Absolute, out var uri)
                        ? uri
                        : new Uri("about:blank"),
                    CreatedAt = DateTimeOffset.UtcNow
                }
            },
            DenseScore = score,
            SparseScore = 0.0,
            FusedScore = score
        };
    }
}
