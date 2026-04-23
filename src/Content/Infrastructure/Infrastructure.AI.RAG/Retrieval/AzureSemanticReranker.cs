using Application.AI.Common.Interfaces.RAG;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Azure AI Search semantic ranker implementation of <see cref="IReranker"/>.
/// Uses Azure's built-in semantic ranking to re-score retrieval results by
/// submitting chunk content as search documents and leveraging the semantic
/// configuration on the index. Registered as keyed service <c>"azure_semantic"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Requires the Azure AI Search index to have a semantic configuration named
/// <c>"default"</c> with the <c>content</c> field as the primary content field.
/// Semantic ranking is billed per 1,000 documents processed -- use
/// <c>AppConfig:AI:Rag:Retrieval:RerankTopK</c> to limit candidates.
/// </para>
/// </remarks>
public sealed class AzureSemanticReranker : IReranker
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AzureSemanticReranker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSemanticReranker"/> class.
    /// </summary>
    /// <param name="searchClient">The Azure Search client with semantic ranking enabled.</param>
    /// <param name="logger">The logger instance.</param>
    public AzureSemanticReranker(
        SearchClient searchClient,
        ILogger<AzureSemanticReranker> logger)
    {
        _searchClient = searchClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankedResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0) return [];

        var options = new SearchOptions
        {
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "default",
                QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
            },
            Size = topK,
            Filter = BuildChunkIdFilter(results),
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText: query, options, cancellationToken);

        var semanticScores = new Dictionary<string, double>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            var id = result.Document["id"]?.ToString() ?? string.Empty;
            var score = result.SemanticSearch?.RerankerScore ?? result.Score ?? 0.0;
            semanticScores[id] = score;
        }

        var reranked = results
            .Select((r, i) => new
            {
                Result = r,
                OriginalRank = i + 1,
                RerankScore = semanticScores.TryGetValue(r.Chunk.Id, out var s)
                    ? NormalizeSemanticScore(s)
                    : 0.0,
            })
            .OrderByDescending(r => r.RerankScore)
            .Take(topK)
            .Select((r, i) => new RerankedResult
            {
                RetrievalResult = r.Result,
                RerankScore = r.RerankScore,
                OriginalRank = r.OriginalRank,
                RerankRank = i + 1,
            })
            .ToList();

        _logger.LogDebug(
            "Azure semantic reranker processed {Input} candidates, returned {Output} results",
            results.Count, reranked.Count);

        return reranked;
    }

    /// <summary>
    /// Builds an OData filter expression to scope semantic ranking to only the
    /// chunk IDs present in the retrieval results.
    /// </summary>
    private static string BuildChunkIdFilter(IReadOnlyList<RetrievalResult> results)
    {
        var ids = results.Select(r => $"'{r.Chunk.Id}'");
        return $"search.in(id, '{string.Join(",", results.Select(r => r.Chunk.Id))}', ',')";
    }

    /// <summary>
    /// Azure semantic ranker scores are on a 0-4 scale. Normalize to [0, 1].
    /// </summary>
    private static double NormalizeSemanticScore(double score) =>
        Math.Clamp(score / 4.0, 0.0, 1.0);
}
