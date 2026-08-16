using Domain.AI.Governance;

namespace Domain.AI.Escalation;

/// <summary>
/// The resolved result of an escalation request. Created when sufficient approver
/// decisions have been collected, the request times out, or it is escalated.
/// </summary>
public sealed record EscalationOutcome
{
    /// <summary>Correlates back to the originating <see cref="EscalationRequest"/>.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>Final approval verdict.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>Individual approver decisions collected during the escalation.</summary>
    public required IReadOnlyList<ApproverDecision> Decisions { get; init; }

    /// <summary>How the escalation was resolved.</summary>
    public required EscalationResolutionType ResolutionType { get; init; }

    /// <summary>When the escalation was resolved.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>
    /// If resolution was <see cref="EscalationResolutionType.Escalated"/>, the autonomy tier the
    /// originating request could not clear — i.e. why escalation was needed. Copied from
    /// <see cref="EscalationRequest.EscalationTierTarget"/>; not a grant of that tier. What
    /// approving the resulting tier-2 escalation actually unlocks is entirely up to the
    /// caller-owned downstream process that raises it. Null otherwise.
    /// </summary>
    public AutonomyLevel? EscalatedToTier { get; init; }

    /// <summary>
    /// The approver roster carried over from the originating request. The escalation service
    /// populates this on every resolution so roster-private reads keep working after the pending
    /// request is discarded: a resolved verdict is only visible to the identities that were
    /// entitled to produce it. An empty roster means no roster is known, and roster-gated reads
    /// deny (fail-closed).
    /// </summary>
    public IReadOnlyList<string> Approvers { get; init; } = [];

    /// <summary>
    /// The identity that administratively cancelled the escalation, or null when it resolved by
    /// decision or timeout. Carried on the outcome — and therefore into the durable outcome
    /// audit record — so a force-denial is always attributable to its actor.
    /// </summary>
    public string? CancelledBy { get; init; }
}
