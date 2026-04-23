using Application.AI.Common.Interfaces.RAG;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Azure AI Search implementation of <see cref="IBm25Store"/> using the built-in
/// full-text BM25 scoring. Shares the same index as <see cref="AzureAISearchVectorStore"/>
/// but queries using <c>SearchText</c> instead of vector similarity. Registered as
/// keyed service <c>"azure_ai_search"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The index must include a <c>content</c> field configured as searchable for BM25
/// scoring. The <c>embedding</c> field is not used by this store -- it only performs
/// keyword-based full-text search.
/// </para>
/// </remarks>
public sealed class AzureAISearchBm25Store : IBm25Store
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AzureAISearchBm25Store> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAISearchBm25Store"/> class.
    /// </summary>
    /// <param name="searchClient">The Azure Search client configured for the target index.</param>
    /// <param name="logger">The logger instance.</param>
    public AzureAISearchBm25Store(
        SearchClient searchClient,
        ILogger<AzureAISearchBm25Store> logger)
    {
        _searchClient = searchClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task IndexAsync(
        IReadOnlyList<DocumentChunk> chunks,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        var documents = chunks.Select(chunk => new SearchDocument
        {
            ["id"] = chunk.Id,
            ["documentId"] = chunk.DocumentId,
            ["content"] = chunk.Content,
            ["sectionPath"] = chunk.SectionPath,
        }).ToList();

        var response = await _searchClient.MergeOrUploadDocumentsAsync(
            documents, cancellationToken: cancellationToken);

        _logger.LogDebug(
            "Indexed {Count} chunks for BM25 into Azure AI Search, {Succeeded} succeeded",
            chunks.Count, response.Value.Results.Count(r => r.Succeeded));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchOptions
        {
            Size = topK,
            QueryType = SearchQueryType.Simple,
            Select = { "id", "documentId", "content", "sectionPath" },
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText: query, options, cancellationToken);

        var results = new List<RetrievalResult>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            var chunk = MapToChunk(result.Document);
            var score = result.Score ?? 0.0;

            results.Add(new RetrievalResult
            {
                Chunk = chunk,
                DenseScore = 0.0,
                SparseScore = NormalizeBm25Score(score),
                FusedScore = NormalizeBm25Score(score),
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string documentId,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchOptions
        {
            Filter = $"documentId eq '{documentId.Replace("'", "''")}'",
            Select = { "id" },
            Size = 1000,
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText: "*", options, cancellationToken);

        var keysToDelete = new List<SearchDocument>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            keysToDelete.Add(new SearchDocument { ["id"] = result.Document["id"] });
        }

        if (keysToDelete.Count > 0)
        {
            await _searchClient.DeleteDocumentsAsync(
                keysToDelete, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Deleted {Count} BM25-indexed chunks for document {DocumentId}",
                keysToDelete.Count, documentId);
        }
    }

    private static DocumentChunk MapToChunk(SearchDocument doc) => new()
    {
        Id = doc["id"]?.ToString() ?? string.Empty,
        DocumentId = doc["documentId"]?.ToString() ?? string.Empty,
        Content = doc["content"]?.ToString() ?? string.Empty,
        SectionPath = doc["sectionPath"]?.ToString() ?? string.Empty,
        Tokens = 0,
        Metadata = new ChunkMetadata
        {
            SourceUri = new Uri("search://azure-ai-search"),
            CreatedAt = DateTimeOffset.UtcNow,
        },
    };

    /// <summary>
    /// Normalizes Azure AI Search BM25 scores to [0, 1] using a sigmoid-like
    /// transformation. Azure Search scores are unbounded positive values.
    /// </summary>
    private static double NormalizeBm25Score(double score) =>
        score / (1.0 + score);
}
