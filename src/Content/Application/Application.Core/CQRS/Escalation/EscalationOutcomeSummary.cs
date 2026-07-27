using Domain.AI.Escalation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// A wire-safe projection of a resolved <see cref="EscalationOutcome"/>: the final verdict, how
/// it was reached, and the individual decisions that produced it. Excludes
/// <see cref="EscalationOutcome.EscalatedToTier"/>'s governance internals only in the sense of
/// projecting it as-is — the tier enum is already a public domain concept.
/// </summary>
public sealed record EscalationOutcomeSummary
{
    /// <summary>Correlates back to the originating escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>Final approval verdict.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>How the escalation was resolved (approved, denied, timed out, escalated).</summary>
    public required EscalationResolutionType ResolutionType { get; init; }

    /// <summary>When the escalation was resolved.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>The individual approver decisions collected before resolution.</summary>
    public required IReadOnlyList<ApproverDecisionSummary> Decisions { get; init; }

    /// <summary>Projects a domain <see cref="EscalationOutcome"/> to the wire-safe shape.</summary>
    /// <param name="outcome">The resolved outcome to project. Must not be null.</param>
    public static EscalationOutcomeSummary FromOutcome(EscalationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new EscalationOutcomeSummary
        {
            EscalationId = outcome.EscalationId,
            IsApproved = outcome.IsApproved,
            ResolutionType = outcome.ResolutionType,
            ResolvedAt = outcome.ResolvedAt,
            Decisions = outcome.Decisions.Select(ApproverDecisionSummary.FromDecision).ToList()
        };
    }
}
