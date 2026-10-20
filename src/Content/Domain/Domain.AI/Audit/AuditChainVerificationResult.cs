namespace Domain.AI.Audit;

/// <summary>
/// The outcome of verifying the integrity of a hash-chained audit log.
/// </summary>
/// <remarks>
/// <para>
/// A tamper-evident audit log links every record to its predecessor via a cryptographic
/// hash. Verification walks the chain from genesis and recomputes each link; any retroactive
/// edit, deletion, or reordering breaks the chain at the first affected record. This type
/// reports whether the chain held and, when it did not, exactly where it broke.
/// </para>
/// <para>
/// <see cref="VerifiedCount"/> counts the records that verified cleanly before any break,
/// so an operator can see how much of the log is still trustworthy. <see cref="FirstBrokenSequence"/>
/// pinpoints the first record whose hash, previous-hash link, or sequence number did not match,
/// which is the record that was tampered with or the gap left by a deleted record.
/// </para>
/// <para>
/// <b>A pass need not start at genesis (#416).</b> Once a log's finished files are archived, a pass reads from the
/// archive's newest record on; the archive's earlier records were checked when they were archived.
/// <see cref="StartSequence"/> says where this pass started, so <see cref="VerifiedCount"/> is never mistaken for the
/// size of the whole log.
/// </para>
/// </remarks>
public sealed record AuditChainVerificationResult
{
    /// <summary>Gets whether the entire chain verified without a break.</summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Gets the sequence number this pass started verifying at: <c>0</c> when it read from genesis, higher when the
    /// earlier records are archived (#416).
    /// </summary>
    public required long StartSequence { get; init; }

    /// <summary>Gets the number of records that verified cleanly from <see cref="StartSequence"/> before any break.</summary>
    public required long VerifiedCount { get; init; }

    /// <summary>
    /// Gets the sequence number of the first record where the chain broke, or <c>null</c>
    /// when the chain is valid.
    /// </summary>
    public long? FirstBrokenSequence { get; init; }

    /// <summary>
    /// Gets a human-readable explanation of the break (hash mismatch, sequence gap, or
    /// malformed line), or <c>null</c> when the chain is valid.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Gets the sequence number of the last record that verified cleanly, or <c>-1</c> when none did.
    /// Recorded as the next pass's <see cref="AuditChainCheckpoint"/> (#416).
    /// </summary>
    public long HeadSequence { get; init; } = -1;

    /// <summary>Gets the hash of the record at <see cref="HeadSequence"/>, or <c>null</c> when none verified.</summary>
    public string? HeadHash { get; init; }

    /// <summary>Creates a result indicating the chain verified cleanly.</summary>
    /// <param name="startSequence">The sequence number the pass started at (<c>0</c> from genesis).</param>
    /// <param name="verifiedCount">The number of records verified.</param>
    /// <param name="headSequence">The sequence number of the newest verified record, or <c>-1</c> when none.</param>
    /// <param name="headHash">The newest verified record's hash, or <c>null</c> when none.</param>
    public static AuditChainVerificationResult Valid(
        long startSequence, long verifiedCount, long headSequence = -1, string? headHash = null) =>
        new()
        {
            IsValid = true,
            StartSequence = startSequence,
            VerifiedCount = verifiedCount,
            HeadSequence = headSequence,
            HeadHash = headHash
        };

    /// <summary>Creates a result indicating the chain broke.</summary>
    /// <param name="startSequence">The sequence number the pass started at (<c>0</c> from genesis).</param>
    /// <param name="verifiedCount">The number of records that verified before the break.</param>
    /// <param name="firstBrokenSequence">The sequence number where the break was detected.</param>
    /// <param name="failureReason">A human-readable explanation of the break.</param>
    public static AuditChainVerificationResult Broken(
        long startSequence, long verifiedCount, long firstBrokenSequence, string failureReason) =>
        new()
        {
            IsValid = false,
            StartSequence = startSequence,
            VerifiedCount = verifiedCount,
            FirstBrokenSequence = firstBrokenSequence,
            FailureReason = failureReason
        };
}
