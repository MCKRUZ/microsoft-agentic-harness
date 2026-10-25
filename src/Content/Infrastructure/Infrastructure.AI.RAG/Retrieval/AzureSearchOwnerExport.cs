using System.Text.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Domain.Common.Compliance;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Reads every chunk an owner ingested from the Azure AI Search index, for the personal-data export (#902). Shared by the
/// vector and BM25 stores, which use the same index, the way <see cref="AzureSearchOwnerErasure"/> is.
/// </summary>
[PersonalDataChannel("Reads an owner's chunks from the Azure AI Search index for the vector and BM25 stores that call it; they describe the data.")]
internal static class AzureSearchOwnerExport
{
    /// <summary>The name both Azure stores export under, so the coordinator reads the shared index once.</summary>
    public const string StoreName = "document index (Azure AI Search)";

    private const int PageSize = 1000;

    /// <summary>The service refuses a <c>$skip</c> above this; a person with more chunks cannot be read this way.</summary>
    private const int MaxSkip = 100_000;

    private static readonly string[] ExportedFields = ["id", "documentId", "content", "sectionPath"];

    /// <summary>
    /// Every chunk stamped with <paramref name="ownerId"/> as issued, through the same filter erasure deletes by. Each
    /// record carries the chunk's text, document and section; never its embedding, which is a derived vector of the same
    /// text, nor the owner and tenant stamps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reads in pages and checks the total.</strong> A search returns at most a page, and unlike erasure a read
    /// cannot advance by removing what it matched, so it pages with <c>$skip</c> and asks for the total count. Chunks are
    /// collected by id, so one repeated across pages counts once. If the ids read do not add up to the total (the index
    /// changed during the read, or the service ordered ties differently between pages), or the total is beyond what
    /// <c>$skip</c> can reach, it throws: the coordinator then reports this store as failed rather than hand over a copy
    /// with chunks silently missing.
    /// </para>
    /// </remarks>
    /// <param name="searchClient">The client for the index both stores use.</param>
    /// <param name="ownerId">The owner as issued, the form ingest stamps.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One record per chunk, ordered by id.</returns>
    /// <exception cref="InvalidOperationException">The chunks read could not be confirmed to be all of them.</exception>
    public static async Task<IReadOnlyList<JsonElement>> ReadByOwnerAsync(
        SearchClient searchClient, string ownerId, CancellationToken cancellationToken)
    {
        var chunks = new SortedDictionary<string, ExportedChunk>(StringComparer.Ordinal);
        long? total = null;
        for (var skip = 0; ; skip += PageSize)
        {
            var options = new SearchOptions
            {
                Filter = AzureSearchOwnerErasure.EqualsFilter("ownerId", ownerId),
                Size = PageSize,
                Skip = skip,
                IncludeTotalCount = true,
            };
            foreach (var field in ExportedFields)
                options.Select.Add(field);

            var response = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
            total ??= response.Value.TotalCount;
            if (total > MaxSkip + PageSize)
                throw new InvalidOperationException($"More chunks match than Azure AI Search can page through ({total}).");

            var matched = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                matched++;
                var chunk = ToChunk(result.Document);
                chunks.TryAdd(chunk.Id, chunk);
            }

            if (matched < PageSize)
                break;
        }

        if (total is null || chunks.Count != total)
        {
            throw new InvalidOperationException(
                $"Azure AI Search returned {chunks.Count} distinct chunks for a total of {total?.ToString() ?? "unknown"}; the copy could not be confirmed complete.");
        }

        return [.. chunks.Values.Select(c => JsonSerializer.SerializeToElement(c, JsonSerializerOptions.Web))];
    }

    private static ExportedChunk ToChunk(SearchDocument document) => new(
        document["id"] as string ?? string.Empty,
        document.TryGetValue("documentId", out var documentId) ? documentId as string : null,
        document.TryGetValue("content", out var content) ? content as string : null,
        document.TryGetValue("sectionPath", out var sectionPath) ? sectionPath as string : null);

    /// <summary>One chunk as it appears in a person's copy.</summary>
    /// <param name="Id">The chunk's id.</param>
    /// <param name="DocumentId">The document it was cut from.</param>
    /// <param name="Content">The chunk's text.</param>
    /// <param name="SectionPath">Where in the document it sits.</param>
    private sealed record ExportedChunk(string Id, string? DocumentId, string? Content, string? SectionPath);
}
