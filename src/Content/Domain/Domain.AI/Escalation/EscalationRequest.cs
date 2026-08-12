using Domain.AI.Governance;

namespace Domain.AI.Escalation;

/// <summary>
/// A structured request for human approval of an agent action that exceeds its authority.
/// Built from a <see cref="GovernanceDecision"/> with <c>RequireApproval</c> action,
/// or from an <see cref="AutonomyExceededResult"/> during delegation.
/// </summary>
public sealed record EscalationRequest
{
    /// <summary>Unique identifier for this escalation.</summary>
    public required Guid EscalationId { get; init; }

    /// <summary>The agent that attempted the action.</summary>
    public required string AgentId { get; init; }

    /// <summary>The tool or operation the agent tried to invoke.</summary>
    public required string ToolName { get; init; }

    /// <summary>Arguments passed to the tool (sanitized for audit display).</summary>
    public required IReadOnlyDictionary<string, string> Arguments { get; init; }

    /// <summary>Human-readable summary of the attempted action.</summary>
    public required string Description { get; init; }

    /// <summary>Risk level derived from the matched governance rule.</summary>
    public required RiskLevel RiskLevel { get; init; }

    /// <summary>Urgency of this escalation, drives timeout and notification behavior.</summary>
    public required EscalationPriority Priority { get; init; }

    /// <summary>Strategy for evaluating multiple approver decisions.</summary>
    public ApprovalStrategyType ApprovalStrategy { get; init; } = ApprovalStrategyType.AnyOf;

    /// <summary>Ordered list of approver identifiers.</summary>
    public required IReadOnlyList<string> Approvers { get; init; }

    /// <summary>For Quorum strategy, the N in N-of-M required approvals.</summary>
    public int QuorumThreshold { get; init; }

    /// <summary>Seconds before this escalation expires.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Action to take when the escalation times out.</summary>
    public EscalationTimeoutAction TimeoutAction { get; init; } = EscalationTimeoutAction.DenyAndEscalate;

    /// <summary>When the escalation was created.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// The governance decision that triggered this escalation. Null when triggered
    /// by an <see cref="AutonomyExceededResult"/> from the supervisor.
    /// </summary>
    public GovernanceDecision? OriginatingDecision { get; init; }

    /// <summary>
    /// Which attempt at this action this is, 1-based. Greater than 1 means a prior
    /// <em>approved</em> attempt at the same action in the same conversation ran and failed —
    /// not a revision, not a resubmission of an unresolved request. Defaults to 1 so every
    /// existing caller is unaffected; only <c>EscalationToolApprovalRouter</c> currently
    /// populates a higher value, from bounded conversation-scoped failure memory.
    /// </summary>
    public int AttemptNumber { get; init; } = 1;

    /// <summary>
    /// Why the previous attempt at this action failed, shown to the approver so a corrected
    /// retry is never indistinguishable from being asked the same question twice. Null on a
    /// first attempt. Approver-facing only — never relayed to the model.
    /// </summary>
    public string? PriorFailureReason { get; init; }

    /// <summary>
    /// The escalation id of the failed prior attempt this one follows, when
    /// <see cref="AttemptNumber"/> is greater than 1. Null on a first attempt.
    /// </summary>
    public Guid? PredecessorEscalationId { get; init; }
}
