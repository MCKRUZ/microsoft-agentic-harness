using Azure.Search.Documents;
using Domain.Common.Compliance;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Removes chunks from an Azure AI Search index by a field written at ingest: every chunk an owner ingested, by the
/// <c>ownerId</c> stamp (#900), or every chunk of one document, by <c>documentId</c> (#901). Shared by the vector and BM25
/// stores, which use the same index.
/// </summary>
[PersonalDataChannel("Deletes an owner's chunks from the Azure AI Search index for the vector and BM25 stores that call it; they describe the data.")]
internal static class AzureSearchOwnerErasure
{
    private const int PageSize = 1000;
    private const int MaxStaleRounds = 5;
    private static readonly TimeSpan StaleRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Deletes every chunk stamped with <paramref name="ownerId"/> and returns how many were confirmed deleted.</summary>
    /// <remarks>See <see cref="DeleteMatchingAsync"/> for how it pages and fails.</remarks>
    public static Task<int> DeleteByOwnerAsync(
        SearchClient searchClient, string ownerId, ILogger logger, CancellationToken cancellationToken) =>
        DeleteMatchingAsync(searchClient, "ownerId", ownerId, logger, cancellationToken);

    /// <summary>
    /// Deletes every chunk of <paramref name="documentId"/> and returns how many were confirmed deleted. A single search
    /// page used to be all a document delete removed, so a document of more than a thousand chunks was left part-indexed.
    /// </summary>
    /// <remarks>See <see cref="DeleteMatchingAsync"/> for how it pages and fails.</remarks>
    public static Task<int> DeleteByDocumentAsync(
        SearchClient searchClient, string documentId, ILogger logger, CancellationToken cancellationToken) =>
        DeleteMatchingAsync(searchClient, "documentId", documentId, logger, cancellationToken);

    /// <summary>Deletes every chunk whose <paramref name="field"/> equals <paramref name="value"/>.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Pages until nothing new matches.</strong> A single search returns at most <see cref="PageSize"/> results,
    /// which is why a document-id delete is not enough for a person who ingested more. The index is eventually
    /// consistent, so a page can briefly repeat chunks already deleted. Those are skipped; a page holding nothing but
    /// them is retried after a short wait, a bounded number of times, and then fails rather than report success, because
    /// a full page of stale results can hide chunks still to delete behind it. The loop ends when a page matches nothing.
    /// </para>
    /// <para>
    /// <strong>Any per-chunk failure throws.</strong> A partially failed batch would otherwise be retried forever or
    /// reported as done; throwing makes the erasure receipt say this store failed.
    /// </para>
    /// <para>The value is quoted for OData with single quotes doubled, so a crafted id cannot widen the filter.</para>
    /// </remarks>
    private static async Task<int> DeleteMatchingAsync(
        SearchClient searchClient, string field, string value, ILogger logger, CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Filter = $"{field} eq '{value.Replace("'", "''", StringComparison.Ordinal)}'",
            Select = { "id" },
            Size = PageSize,
        };

        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var staleRounds = 0;
        while (true)
        {
            var response = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
            var keys = new List<string>();
            var matched = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                matched++;
                if (result.Document["id"] is string id && !deleted.Contains(id))
                    keys.Add(id);
            }

            if (matched == 0)
                return deleted.Count;

            if (keys.Count == 0)
            {
                // Every match was one already deleted: the index has not caught up, and a full page of those can hide
                // chunks still to delete behind it. Wait briefly; if it never catches up, fail rather than claim done.
                if (++staleRounds > MaxStaleRounds)
                {
                    throw new InvalidOperationException(
                        $"Azure AI Search kept returning chunks already deleted; the remaining chunks matching {field} could not be confirmed gone.");
                }

                await Task.Delay(StaleRetryDelay, cancellationToken);
                continue;
            }

            staleRounds = 0;

            var batch = keys.Select(id => new SearchDocument { ["id"] = id }).ToList();
            var outcome = await searchClient.DeleteDocumentsAsync(batch, cancellationToken: cancellationToken);
            var failed = outcome.Value.Results.Count(r => !r.Succeeded);
            if (failed > 0)
            {
                logger.LogError(
                    "Azure AI Search delete by {Field} failed for {FailedCount} of {Total} chunks.", field, failed, batch.Count);
                throw new InvalidOperationException($"Azure AI Search failed to delete {failed} of {batch.Count} chunks.");
            }

            deleted.UnionWith(keys);
        }
    }
}
