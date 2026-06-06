namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Content-addressed evidence storage. Gates that produce bulk evidence
/// (validator output, policy findings, merge plans) write it here and embed
/// only the returned hash on <c>GateResult.EvidenceHash</c>; audit lines stay
/// small while the full evidence remains recoverable.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent on duplicate writes — two stores of the same content return the
/// same hash and produce no duplicate storage. This lets the same evidence be
/// referenced across multiple gate decisions (e.g. a single policy run feeding
/// both the Policy gate's history entry and a downstream dashboard).
/// </para>
/// </remarks>
public interface IEvidenceStore
{
    /// <summary>
    /// Persist evidence and return its content-addressed hash. The returned
    /// hash is the same format embedded on <c>GateResult.EvidenceHash</c>
    /// (<c>"sha256:"</c>-prefixed Base64URL).
    /// </summary>
    /// <param name="content">The raw evidence bytes.</param>
    /// <param name="contentType">A short MIME-ish hint persisted alongside the content for later interpretation (<c>application/json</c>, <c>text/plain</c>, <c>application/octet-stream</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <c>"sha256:"</c>-prefixed content hash.</returns>
    Task<string> StoreAsync(
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolve evidence by its content-addressed hash. Returns null when no
    /// matching content exists (caller should treat as "evidence not retained"
    /// rather than an error — retention policy may have pruned it).
    /// </summary>
    /// <param name="evidenceHash">The hash returned by <see cref="StoreAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evidence bytes, or null when not found.</returns>
    Task<ReadOnlyMemory<byte>?> RetrieveAsync(
        string evidenceHash,
        CancellationToken cancellationToken);
}
