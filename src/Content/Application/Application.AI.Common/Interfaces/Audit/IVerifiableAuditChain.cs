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
    /// Verifies the integrity of this audit chain end-to-end.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification outcome, including the first broken sequence when tampered.</returns>
    Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken);
}
