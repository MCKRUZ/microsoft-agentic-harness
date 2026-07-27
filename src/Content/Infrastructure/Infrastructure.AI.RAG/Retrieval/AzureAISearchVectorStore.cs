using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Azure AI Search implementation of <see cref="IVectorStore"/> using the
/// <c>Azure.Search.Documents</c> SDK. Supports vector similarity search via
/// <see cref="VectorizedQuery"/> and upsert-based indexing for idempotent
/// re-ingestion. Registered as keyed service <c>"azure_ai_search"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The index must be pre-provisioned with a schema that includes:
/// <list type="bullet">
///   <item><c>id</c> — string key field (chunk ID).</item>
///   <item><c>documentId</c> — filterable string for document-level deletion.</item>
///   <item><c>content</c> — searchable string for BM25 full-text search.</item>
///   <item><c>sectionPath</c> — searchable string for hierarchical navigation.</item>
///   <item><c>embedding</c> — vector field matching the configured dimensions.</item>
///   <item><c>ownerId</c>, <c>tenantId</c> — filterable strings for the owner/tenant
///     provenance stamps (the future erasure key). Written only when the ingesting caller
///     carries an identity, so an index without these fields keeps working for
///     identity-less ingest; a stamped ingest against such an index fails loudly rather
///     than silently dropping the erasure key. The stamps are <strong>never returned on
///     search results</strong>: they are erasure/audit data, and surfacing them would tell
///     every searcher who ingested each chunk in the shared corpus.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Collections are not honored.</strong> This store always queries the one index
/// its <see cref="SearchClient"/> was built for; the <c>collectionName</c> parameter is
/// ignored. <c>RagConfigValidator</c> therefore rejects
/// <c>AppConfig:AI:Rag:ScopedCollections</c> with this provider — per-tenant isolation on
/// Azure requires per-tenant index provisioning, which is an infrastructure concern.
/// </para>
/// </remarks>
public sealed class AzureAISearchVectorStore : IVectorStore
{
    private readonly SearchClient _searchClient;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<AzureAISearchVectorStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAISearchVectorStore"/> class.
    /// </summary>
    /// <param name="searchClient">The Azure Search client configured for the target index.</param>
    /// <param name="embeddingService">The embedding service for query vectorization.</param>
    /// <param name="logger">The logger instance.</param>
    public AzureAISearchVectorStore(
        SearchClient searchClient,
        IEmbeddingService embeddingService,
        ILogger<AzureAISearchVectorStore> logger)
    {
        _searchClient = searchClient;
        _embeddingService = embeddingService;
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

            if (chunk.Embedding is { Count: > 0 })
            {
                doc["embedding"] = chunk.Embedding.ToArray();
            }

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

        _logger.LogDebug(
            "Indexed {Count} chunks into Azure AI Search, {Succeeded} succeeded",
            chunks.Count, response.Value.Results.Count(r => r.Succeeded));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var vectorQuery = new VectorizedQuery(queryEmbedding)
        {
            KNearestNeighborsCount = topK,
            Fields = { "embedding" },
        };

        var options = new SearchOptions
        {
            Size = topK,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { vectorQuery },
            },
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText: null, options, cancellationToken);

        var results = new List<RetrievalResult>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            var chunk = MapToChunk(result.Document);
            var score = result.Score ?? 0.0;

            results.Add(new RetrievalResult
            {
                Chunk = chunk,
                DenseScore = NormalizeScore(score),
                SparseScore = 0.0,
                FusedScore = NormalizeScore(score),
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
                "Deleted {Count} chunks for document {DocumentId} from Azure AI Search",
                keysToDelete.Count, documentId);
        }
    }

    // ownerId/tenantId are deliberately NOT mapped: provenance stamps stay in the index
    // for erasure and are never exposed on the read path.
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

    private static double NormalizeScore(double score) => Math.Clamp(score, 0.0, 1.0);
}
