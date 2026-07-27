using Domain.AI.DriftDetection;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.DriftDetection;

/// <summary>
/// Pushes one set of dimension scores into the drift subsystem: the scores are compared against
/// the active baseline, EWMA state advances, the evaluation is persisted to history, and —
/// above threshold — notifications and escalations fire. Returns the resulting
/// <see cref="DriftScore"/>.
/// </summary>
/// <remarks>
/// This is a history-poisoning vector: pushed scores feed the EWMA smoothing and the rolling
/// window future baselines are recalculated from, so a hostile caller could mask real drift or
/// fabricate it. The HTTP surface therefore gates this command behind the
/// <c>Harness.Drift.Operate</c> role, and the handler records every push (including failed
/// ones) in the drift audit trail with the token-derived caller identity.
/// </remarks>
public sealed record PushDriftEvaluationCommand : IRequest<Result<DriftScore>>
{
    /// <summary>The hierarchy level of the evaluation.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope (agent ID, skill name, or task type).</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>
    /// Dimension scores to evaluate against the baseline. Each value must be a finite quality
    /// score in [0, 1].
    /// </summary>
    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }

    /// <summary>
    /// The pushing caller's identity, recorded in the audit trail.
    /// </summary>
    /// <remarks>
    /// <b>Populated exclusively by the controller from the authenticated principal's token
    /// claims</b> (the claim type configured by <c>DriftDetectionConfig.CallerIdentityClaimType</c>).
    /// It must never be bound from a request body, query string, or header — no wire DTO
    /// carries a caller-id field by design, so a caller cannot attribute a push to someone else.
    /// </remarks>
    public required string CallerId { get; init; }
}
