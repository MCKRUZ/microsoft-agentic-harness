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
/// <para>
/// Owner/tenant provenance stamps (<c>ownerId</c>/<c>tenantId</c>) are written only when
/// the ingesting caller carries an identity — see the schema notes on
/// <see cref="AzureAISearchVectorStore"/>, which shares this index. Search results never
/// include the stamps (the <c>Select</c> projection is kept to the pre-stamp fields):
/// they are persisted for erasure, not for retrieval, and exposing them would tell every
/// searcher who ingested each chunk.
/// </para>
/// <para>
/// <strong>Collections are not honored.</strong> This store always queries the one index
/// its <see cref="SearchClient"/> was built for; the <c>collectionName</c> parameter is
/// ignored, and <c>RagConfigValidator</c> rejects
/// <c>AppConfig:AI:Rag:ScopedCollections</c> with this provider.
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

        var documents = chunks.Select(chunk =>
        {
            var doc = new SearchDocument
            {
                ["id"] = chunk.Id,
                ["documentId"] = chunk.DocumentId,
                ["content"] = chunk.Content,
                ["sectionPath"] = chunk.SectionPath,
            };

            // Written only when present so identity-less ingest keeps working against
            // indexes provisioned before the provenance fields existed.
            if (chunk.Metadata.OwnerId is not null)
            {
                doc["ownerId"] = chunk.Metadata.OwnerId;
            }

            if (chunk.Metadata.TenantId is not null)
            {
                doc["tenantId"] = chunk.Metadata.TenantId;
            }

            return doc;
        }).ToList();

        var response = await _searchClient.MergeOrUploadDocumentsAsync(
            documents, cancellationToken: cancellationToken);

        // MergeOrUploadDocumentsAsync reports PER-DOCUMENT failures without throwing:
        // treating a partial batch as success would let the ingest command report Success
        // while chunks (and their provenance stamps) silently never landed. Failing the
        // operation triggers the ingest handler's compensation path instead.
        ThrowIfAnyFailed(response.Value, chunks.Count, "BM25 indexing");

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
    public async Task<int> DeleteAsync(
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

        if (keysToDelete.Count == 0) return 0;

        var deleteResponse = await _searchClient.DeleteDocumentsAsync(
            keysToDelete, cancellationToken: cancellationToken);

        // Only confirmed deletions count — erasure receipts must never over-report.
        var deleted = deleteResponse.Value.Results.Count(r => r.Succeeded);
        var failed = keysToDelete.Count - deleted;
        if (failed > 0)
        {
            _logger.LogError(
                "Azure AI Search BM25 delete for document {DocumentId} failed for {FailedCount} of {Total} chunks",
                documentId, failed, keysToDelete.Count);
        }

        _logger.LogInformation(
            "Deleted {Count} BM25-indexed chunks for document {DocumentId}",
            deleted, documentId);

        return deleted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Azure store queries one pre-provisioned physical index and the collection
    /// parameter has no meaning here (see the class remarks), so the all-collections
    /// erasure delete is exactly the single-index delete.
    /// </remarks>
    public Task<int> DeleteFromAllCollectionsAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(documentId, collectionName: null, cancellationToken);

    /// <summary>
    /// Fails the operation when the Azure batch response reports any per-document
    /// failure, with a structured log naming the failed count.
    /// </summary>
    private void ThrowIfAnyFailed(IndexDocumentsResult result, int total, string operation)
    {
        var failed = result.Results.Count(r => !r.Succeeded);
        if (failed == 0) return;

        _logger.LogError(
            "Azure AI Search {Operation} failed for {FailedCount} of {Total} chunks",
            operation, failed, total);
        throw new InvalidOperationException(
            $"Azure AI Search {operation} failed for {failed} of {total} chunks; " +
            "the batch response reported per-document failures.");
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
