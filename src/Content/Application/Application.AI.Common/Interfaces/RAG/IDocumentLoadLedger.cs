namespace Application.AI.Common.Interfaces.RAG;

/// <summary>
/// When each document was last loaded (#901), so the optional document age limit can find the ones not loaded for too long.
/// </summary>
/// <remarks>
/// <para>
/// Holds a document id and a time only: not who loaded it, from where, or for which tenant, so it needs no erasure of its
/// own. Written by the ingestion pipeline before any store is written, so a document the corpus holds always has a load
/// time; removed when a document is removed completely.
/// </para>
/// <para>
/// A row whose document was never stored (an ingest that failed after recording it) is harmless: removing it finds nothing
/// and reports the id as unmatched.
/// </para>
/// </remarks>
public interface IDocumentLoadLedger
{
    /// <summary>Records that <paramref name="documentId"/> was loaded now. A later load moves the time forward, never back.</summary>
    /// <param name="documentId">The document loaded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordLoadedAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>Documents last loaded before <paramref name="cutoff"/>, oldest first.</summary>
    /// <param name="cutoff">Documents last loaded strictly before this are returned.</param>
    /// <param name="maxCount">The most to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document ids.</returns>
    Task<IReadOnlyList<string>> ListLoadedBeforeAsync(DateTimeOffset cutoff, int maxCount, CancellationToken cancellationToken);

    /// <summary>When <paramref name="documentId"/> was last loaded, or <see langword="null"/> when there is no record.</summary>
    /// <param name="documentId">The document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last load time, or <see langword="null"/>.</returns>
    Task<DateTimeOffset?> GetLastLoadedAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets <paramref name="documentId"/> unless it was loaded after <paramref name="loadedNoLaterThan"/>, so a document
    /// loaded again while it was being removed keeps its record.
    /// </summary>
    /// <param name="documentId">The document removed.</param>
    /// <param name="loadedNoLaterThan">When the removal started.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a record was forgotten.</returns>
    Task<bool> ForgetAsync(string documentId, DateTimeOffset loadedNoLaterThan, CancellationToken cancellationToken);
}
