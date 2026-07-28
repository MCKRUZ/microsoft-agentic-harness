using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Recalculates the baseline identified by <see cref="BaselineId"/> from its scope's recent
/// evaluation history (the subsystem's configured rolling window), replacing the current
/// snapshot. Returns the new <see cref="DriftBaseline"/>.
/// </summary>
/// <remarks>
/// Recalculating a baseline re-anchors what "normal" means for the scope — a hostile
/// recalculation after a run of poisoned evaluations would normalize the poison. The HTTP
/// surface gates this behind the <c>Harness.Drift.Operate</c> role and the handler records
/// every request (including unknown-id and insufficient-history failures) in the audit trail
/// with the token-derived caller identity.
/// </remarks>
public sealed record RecalculateDriftBaselineCommand : IRequest<Result<DriftBaseline>>
{
    /// <summary>The id of the baseline snapshot to recalculate. Unknown ids yield a not-found failure.</summary>
    public required Guid BaselineId { get; init; }

    /// <summary>
    /// The requesting caller's identity, recorded in the audit trail.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>DriftDetectionConfig.CallerIdentityClaimType</c>).
    /// It must never be bound from a request body, query string, or header.
    /// </remarks>
    public required string CallerId { get; init; }
}
