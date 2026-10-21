using Azure.Search.Documents;
using Domain.Common.Compliance;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Removes every chunk an owner ingested from an Azure AI Search index, by the <c>ownerId</c> stamp written at ingest
/// (#900). Shared by the vector and BM25 stores, which use the same index.
/// </summary>
[PersonalDataChannel("Deletes an owner's chunks from the Azure AI Search index for the vector and BM25 stores that call it; they describe the data.")]
internal static class AzureSearchOwnerErasure
{
    private const int PageSize = 1000;
    private const int MaxStaleRounds = 5;
    private static readonly TimeSpan StaleRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Deletes every chunk stamped with <paramref name="ownerId"/> and returns how many were confirmed deleted.</summary>
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
    /// <para>The owner is quoted for OData with single quotes doubled, so a crafted id cannot widen the filter.</para>
    /// </remarks>
    public static async Task<int> DeleteByOwnerAsync(
        SearchClient searchClient, string ownerId, ILogger logger, CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Filter = $"ownerId eq '{ownerId.Replace("'", "''", StringComparison.Ordinal)}'",
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
                        "Azure AI Search kept returning chunks already deleted; the owner's remaining chunks could not be confirmed gone.");
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
                    "Azure AI Search owner erasure failed for {FailedCount} of {Total} chunks.", failed, batch.Count);
                throw new InvalidOperationException($"Azure AI Search failed to delete {failed} of {batch.Count} chunks.");
            }

            deleted.UnionWith(keys);
        }
    }
}
