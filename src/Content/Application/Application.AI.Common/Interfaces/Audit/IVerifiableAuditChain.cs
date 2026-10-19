using Domain.AI.Audit;

namespace Application.AI.Common.Interfaces.Audit;

/// <summary>
/// A hash-chained audit log whose integrity can be verified on demand. Implemented by the
/// JSONL audit sinks so the scheduled chain-verification service can walk every chain without
/// knowing each sink's record shape.
/// </summary>
/// <remarks>
/// Verification recomputes the cryptographic links from genesis; a clean result means no record
/// has been edited, deleted, or reordered since it was written. The scheduled verifier turns a
/// broken result into an operator alert — without it, the hash-chain exists only on paper.
/// </remarks>
public interface IVerifiableAuditChain
{
    /// <summary>
    /// Gets a stable, human-readable name for this audit chain (e.g. <c>"changes"</c>,
    /// <c>"egress"</c>) used in verification metrics, logs, and receipts.
    /// </summary>
    string AuditName { get; }

    /// <summary>
    /// Gets where this chain is stored: its file, or its folder when it spans dated segments. Keys the
    /// chain's verification checkpoint, so a log that is moved or reset starts a fresh baseline instead
    /// of being reported as truncated (#416).
    /// </summary>
    string AuditLocation { get; }

    /// <summary>
    /// Verifies the integrity of this audit chain end-to-end.
    /// </summary>
    /// <param name="checkpoint">
    /// The point the chain was last verified up to, or <c>null</c> for none. When given, a chain that no
    /// longer reaches it, or whose record there differs, is reported broken: that is how truncation and
    /// a deleted-and-regrown chain are caught, which internal consistency alone cannot see (#416).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification outcome, including the first broken sequence when tampered.</returns>
    Task<AuditChainVerificationResult> VerifyChainAsync(AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken);
}
