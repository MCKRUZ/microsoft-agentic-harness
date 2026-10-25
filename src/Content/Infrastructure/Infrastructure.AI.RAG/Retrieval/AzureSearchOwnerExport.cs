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

    private static readonly string[] ExportedFields = ["id", "documentId", "content", "sectionPath"];

    /// <summary>
    /// Every chunk stamped with <paramref name="ownerId"/> as issued, through the same filter erasure deletes by. Each
    /// record carries the chunk's text, document and section; never its embedding, which is a derived vector of the same
    /// text, nor the owner and tenant stamps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Pages by id, then checks the total.</strong> A search returns at most a page, and unlike erasure a read
    /// cannot advance by removing what it matched. Paging with <c>$skip</c> does not work either: every chunk of a
    /// <c>*</c> search scores the same, so the service may order ties differently from one page to the next, and
    /// <c>$skip</c> stops at 100,000. So each page is ordered by <c>id</c> and starts after the last id read, which reads
    /// every chunk exactly once however many there are. This needs the index's <c>id</c> field to be sortable and
    /// filterable (the REST API's default for a string field; the .NET SDK's <c>SimpleField</c> leaves sortable off); on an
    /// index where it is not, the service refuses the query and this store is reported as failed. The first page also
    /// asks for the total count, and if the chunks read do not add up to it (the index changed during the read), it
    /// throws rather than hand over a copy with chunks silently missing.
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
        string? after = null;
        while (true)
        {
            var response = await searchClient.SearchAsync<SearchDocument>(
                "*", PageOptions(ownerId, after, includeTotal: total is null), cancellationToken);
            total ??= response.Value.TotalCount;

            var matched = 0;
            var added = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                matched++;
                var chunk = ToChunk(result.Document);
                if (chunks.TryAdd(chunk.Id, chunk))
                    added++;
                after = chunk.Id;
            }

            if (matched < PageSize)
                break;

            // A full page that read nothing new means the service did not honour the id bound; another request would
            // return the same page forever.
            if (added == 0)
                throw new InvalidOperationException("Azure AI Search returned a page with no new chunks; the read could not advance.");
        }

        if (total is null || chunks.Count != total)
        {
            throw new InvalidOperationException(
                $"Azure AI Search returned {chunks.Count} distinct chunks for a total of {total?.ToString() ?? "unknown"}; the copy could not be confirmed complete.");
        }

        return [.. chunks.Values.Select(c => JsonSerializer.SerializeToElement(c, JsonSerializerOptions.Web))];
    }

    /// <summary>One page of the owner's chunks in id order, starting after <paramref name="after"/>.</summary>
    private static SearchOptions PageOptions(string ownerId, string? after, bool includeTotal)
    {
        var owner = AzureSearchOwnerErasure.EqualsFilter("ownerId", ownerId);
        var options = new SearchOptions
        {
            Filter = after is null ? owner : $"{owner} and id gt '{after.Replace("'", "''", StringComparison.Ordinal)}'",
            Size = PageSize,
            IncludeTotalCount = includeTotal,
        };
        options.OrderBy.Add("id asc");
        foreach (var field in ExportedFields)
            options.Select.Add(field);
        return options;
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
