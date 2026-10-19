using Domain.AI.Audit;

namespace Application.AI.Common.Interfaces.Audit;

/// <summary>
/// The last point each hash-chained audit log was verified up to (#416), shared by the scheduled
/// verifier, which records it, and the compliance report, which reads it without advancing it.
/// </summary>
/// <remarks>
/// A checkpoint belongs to one chain at one location: moving or resetting a log's storage starts a new
/// checkpoint rather than reporting the moved log as truncated. A chain with no checkpoint is verified
/// for internal consistency only.
/// </remarks>
public interface IAuditChainCheckpoints
{
    /// <summary>
    /// The last checkpoint recorded for <paramref name="chain"/> at its current location, or <c>null</c>
    /// when there is none. Never throws: an unreadable checkpoint reads as none and is logged.
    /// </summary>
    /// <param name="chain">The chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AuditChainCheckpoint?> ReadAsync(IVerifiableAuditChain chain, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="checkpoint"/> as the last verified point for <paramref name="chain"/>.
    /// Never throws: a failure to record is logged and the previous checkpoint stands.
    /// </summary>
    /// <param name="chain">The chain.</param>
    /// <param name="checkpoint">The point it was just verified up to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(IVerifiableAuditChain chain, AuditChainCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Where <paramref name="chain"/>'s checkpoint is kept, for an operator who has to re-baseline a log
    /// that was deliberately reset; <c>null</c> when checkpoints are not being kept.
    /// </summary>
    /// <param name="chain">The chain.</param>
    string? LocationOf(IVerifiableAuditChain chain);
}
