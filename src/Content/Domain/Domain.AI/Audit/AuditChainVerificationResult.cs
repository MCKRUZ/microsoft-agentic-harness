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
/// </remarks>
public sealed record AuditChainVerificationResult
{
    /// <summary>Gets whether the entire chain verified without a break.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Gets the number of records that verified cleanly from genesis before any break.</summary>
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

    /// <summary>Creates a result indicating the chain verified cleanly.</summary>
    /// <param name="verifiedCount">The number of records verified.</param>
    public static AuditChainVerificationResult Valid(long verifiedCount) =>
        new() { IsValid = true, VerifiedCount = verifiedCount };

    /// <summary>Creates a result indicating the chain broke.</summary>
    /// <param name="verifiedCount">The number of records that verified before the break.</param>
    /// <param name="firstBrokenSequence">The sequence number where the break was detected.</param>
    /// <param name="failureReason">A human-readable explanation of the break.</param>
    public static AuditChainVerificationResult Broken(
        long verifiedCount, long firstBrokenSequence, string failureReason) =>
        new()
        {
            IsValid = false,
            VerifiedCount = verifiedCount,
            FirstBrokenSequence = firstBrokenSequence,
            FailureReason = failureReason
        };
}
