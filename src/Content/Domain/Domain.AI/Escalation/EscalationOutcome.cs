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
    /// If resolution was <see cref="EscalationResolutionType.Escalated"/>,
    /// which authority tier received the escalated request. Null otherwise.
    /// </summary>
    public AutonomyLevel? EscalatedToTier { get; init; }
}
