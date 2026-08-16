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
    /// The escalation id of the failed prior attempt or the prior revision round this one
    /// follows, when <see cref="AttemptNumber"/> is greater than 1 or <see cref="RevisionRound"/>
    /// is greater than 1. Null on a first attempt. Shared correlation link between the two
    /// independent counters — they track different things (a prior <em>approved</em> attempt that
    /// failed at runtime, versus a prior round of reviewer-requested revision) and must not be
    /// merged into one.
    /// </summary>
    public Guid? PredecessorEscalationId { get; init; }

    /// <summary>
    /// Which round of reviewer revision this is, 1-based. Greater than 1 means a prior escalation
    /// for the same action resolved <see cref="EscalationResolutionType.Revised"/> and this
    /// request is the retry that followed. Defaults to 1 so every existing caller is unaffected.
    /// Distinct from <see cref="AttemptNumber"/>: a revision means the action never ran, so it
    /// must never advance the attempt counter.
    /// </summary>
    public int RevisionRound { get; init; } = 1;

    /// <summary>
    /// The reviewer's instructions from the prior revision round, when <see cref="RevisionRound"/>
    /// is greater than 1. Null on round 1. Unlike <see cref="PriorFailureReason"/>, this text is
    /// designed to be model-visible by the time it reaches an agent — the reviewer wrote it to
    /// steer the retry.
    /// </summary>
    public string? PriorRevisionInstructions { get; init; }

    /// <summary>
    /// If set, and this request times out with <see cref="EscalationTimeoutAction.Escalate"/> or
    /// <see cref="EscalationTimeoutAction.DenyAndEscalate"/>, the resolution is
    /// <see cref="EscalationResolutionType.Escalated"/> (not <see cref="EscalationResolutionType.TimedOut"/>)
    /// and <see cref="EscalationOutcome.EscalatedToTier"/> is stamped with this value — the
    /// autonomy tier the request could not clear, recorded for audit and to signal a caller-owned
    /// downstream process to hand the request to a second, higher-authority roster. This is
    /// <b>not</b> a tier to auto-grant: the escalation service never auto-grants anything for this
    /// value, and a downstream process approving the tier-2 escalation decides for itself what to
    /// unlock. Null for every caller that has no such downstream process (every escalation source
    /// except delegation-autonomy escalation today) — their timeout behavior on
    /// <c>Escalate</c>/<c>DenyAndEscalate</c> is unaffected by this field.
    /// </summary>
    public AutonomyLevel? EscalationTierTarget { get; init; }
}
