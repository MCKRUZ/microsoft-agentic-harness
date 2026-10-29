using System.Collections.Concurrent;
using System.Globalization;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;

namespace Application.Core.Compliance;

/// <summary>
/// What the document age limit last recorded for each document it is still trying to remove (#998), so a retry that ends
/// the way the last attempt did adds nothing to the compliance audit trail, which is never deleted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> A removal that ends <c>Partial</c> keeps the document's load record, so the age limit tries again
/// on every sweep (every six hours by default) until it ends <c>Full</c>. Recorded in full each time, a store that stays
/// down would add an attempt and an outcome per document per sweep to a trail kept for good, and the trail would grow with
/// retries instead of with requests.
/// </para>
/// <para>
/// <strong>What is recorded.</strong> The first attempt at a document is recorded as any removal is: its attempt before
/// anything is removed, its outcome after. A retry of it is recorded only when it ends differently: with another
/// completeness, other stores still failing, another failure, or anything removed at all. Then its attempt (stamped with
/// when it began) and its outcome are written together once it ends, each with <c>retry_of</c> naming the first attempt's
/// correlation id. A retry that ends as the last recorded one did writes nothing.
/// </para>
/// <para>
/// <strong>A retry that dies part-way.</strong> Its attempt was never written, so it leaves no record of its own. The trail
/// still holds the first attempt and the last outcome recorded, and both are still true: the request began, and the
/// document is not fully removed. The next retry that ends differently, or the one that ends <c>Full</c>, is recorded.
/// </para>
/// <para>
/// <strong>Scope.</strong> Only the age limit passes this; an operator's removal is always recorded in full. A document
/// loaded again since is a new request, so it is tracked by the load the age limit judged as well as by its id. Held in
/// memory by the age-limit service: after a restart, the first retry of each document is recorded again, once.
/// </para>
/// </remarks>
public sealed class DocumentRemovalRetries
{
    /// <summary>The metadata key a recorded retry names its first attempt's correlation id under.</summary>
    public const string RetryOfKey = "retry_of";

    private readonly ConcurrentDictionary<string, Tried> _tried = new(StringComparer.Ordinal);

    /// <summary>Gets how many documents are being tracked.</summary>
    public int Count => _tried.Count;

    /// <summary>Forgets every document not in <paramref name="documentIds"/>: removed since, or loaded again.</summary>
    /// <param name="documentIds">The documents the age limit is still trying to remove.</param>
    public void KeepOnly(IReadOnlyCollection<string> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var keep = documentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var documentId in _tried.Keys.Where(id => !keep.Contains(id)))
            _tried.TryRemove(documentId, out _);
    }

    /// <summary>The first attempt and last recorded outcome for this load of the document, or null when there is none.</summary>
    internal Tried? Previous(string documentId, DateTimeOffset load) =>
        _tried.TryGetValue(documentId, out var tried) && tried.Load == load ? tried : null;

    /// <summary>Remembers the outcome just recorded, or forgets the document once it is fully removed.</summary>
    internal void Recorded(string documentId, DateTimeOffset load, string retryOf, string? outcome, bool fullyRemoved)
    {
        if (fullyRemoved)
            _tried.TryRemove(documentId, out _);
        else
            _tried[documentId] = new Tried(load, retryOf, outcome);
    }

    /// <summary>
    /// What a receipt says that a retry must repeat to count as ending the same way, or null when it removed anything,
    /// which is always worth recording.
    /// </summary>
    internal static string? OutcomeOf(ErasureReceipt receipt)
    {
        var removed = receipt.NodesDeleted + receipt.EdgesDeleted + receipt.FeedbackWeightsDeleted
            + receipt.VectorEmbeddingsDeleted + receipt.Bm25DocumentsDeleted + receipt.CrossSessionMemoriesDeleted
            + receipt.Stores.Sum(store => store.ItemsRemoved);
        if (removed > 0)
            return null;

        var stores = receipt.Stores
            .Where(store => store.Status != StoreErasureStatus.Erased)
            .Select(store => $"{store.Store}={store.Status}:{store.Reason}")
            .Order(StringComparer.Ordinal);
        return string.Join('|',
            receipt.Completeness.ToString(),
            receipt.CompletenessReason ?? string.Empty,
            receipt.UnmatchedDocumentIds.Count.ToString(CultureInfo.InvariantCulture),
            string.Join(',', stores));
    }

    /// <summary>The outcome a removal that failed for <paramref name="reason"/> ended with.</summary>
    internal static string FailedOutcome(string reason) => "failed:" + reason;

    /// <summary><paramref name="metadata"/> with <see cref="RetryOfKey"/> naming the first attempt.</summary>
    internal static Dictionary<string, string> MarkRetry(IReadOnlyDictionary<string, string>? metadata, string retryOf) =>
        new(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [RetryOfKey] = retryOf };

    /// <summary>One document's first recorded attempt and the outcome last recorded for it.</summary>
    /// <param name="Load">The load the age limit judged; another load is another request.</param>
    /// <param name="RetryOf">The first attempt's correlation id, which every recorded retry names.</param>
    /// <param name="Outcome">The last recorded outcome, or null when it removed anything and so matches nothing.</param>
    internal sealed record Tried(DateTimeOffset Load, string RetryOf, string? Outcome);
}
