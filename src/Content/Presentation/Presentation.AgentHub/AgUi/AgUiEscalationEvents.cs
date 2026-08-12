using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Signals that an agent action requires human approval. Emitted when the governance
/// pipeline blocks a tool call and creates an escalation request.
/// </summary>
public sealed record EscalationRequestedEvent : AgUiEvent
{
    /// <summary>Unique identifier for this escalation.</summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }

    /// <summary>The agent that attempted the action.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>The tool or operation the agent tried to invoke.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Human-readable summary of the attempted action.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Urgency level (e.g. "Informational", "Blocking", "Critical").</summary>
    [JsonPropertyName("priority")]
    public required string Priority { get; init; }

    /// <summary>Ordered list of approver identifiers.</summary>
    [JsonPropertyName("approvers")]
    public required IReadOnlyList<string> Approvers { get; init; }

    /// <summary>Seconds before this escalation expires.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public required int TimeoutSeconds { get; init; }

    /// <summary>Tool arguments (sanitized for display). Null when omitted.</summary>
    [JsonPropertyName("arguments")]
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }

    /// <summary>
    /// Which attempt at this action this is, 1-based. Greater than 1 means a prior approved
    /// attempt ran and failed. Optional (not required) so this event's shape stays additive
    /// for any client that predates this field.
    /// </summary>
    [JsonPropertyName("attemptNumber")]
    public int? AttemptNumber { get; init; }

    /// <summary>Why the previous attempt at this action failed. Null on a first attempt.</summary>
    [JsonPropertyName("priorFailureReason")]
    public string? PriorFailureReason { get; init; }

    /// <summary>
    /// Which round of reviewer revision this is, 1-based. Greater than 1 means a prior escalation
    /// for this action resolved with a revise verdict. Optional (not required) so this event's
    /// shape stays additive for any client that predates this field, matching the
    /// <see cref="AttemptNumber"/> precedent.
    /// </summary>
    [JsonPropertyName("revisionRound")]
    public int? RevisionRound { get; init; }

    /// <summary>The reviewer's instructions from the prior revision round. Null on round 1.</summary>
    [JsonPropertyName("priorRevisionInstructions")]
    public string? PriorRevisionInstructions { get; init; }
}

/// <summary>
/// Signals that a pending escalation has been resolved (approved, denied, timed out, or escalated).
/// </summary>
public sealed record EscalationResolvedEvent : AgUiEvent
{
    /// <summary>Correlates back to the originating escalation request.</summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }

    /// <summary>Final approval verdict.</summary>
    [JsonPropertyName("isApproved")]
    public required bool IsApproved { get; init; }

    /// <summary>How the escalation was resolved (e.g. "Approved", "Denied", "TimedOut").</summary>
    [JsonPropertyName("resolutionType")]
    public required string ResolutionType { get; init; }

    /// <summary>When the escalation was resolved.</summary>
    [JsonPropertyName("resolvedAt")]
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>Individual approver decisions, if any.</summary>
    [JsonPropertyName("decisions")]
    public IReadOnlyList<AgUiApproverDecision>? Decisions { get; init; }
}

/// <summary>
/// Lightweight wire-format representation of a single approver's decision.
/// </summary>
public sealed record AgUiApproverDecision
{
    /// <summary>Identifier of the approver.</summary>
    [JsonPropertyName("approverName")]
    public required string ApproverName { get; init; }

    /// <summary>
    /// Whether the approver granted approval. Derived from <see cref="Verdict"/> — true only for
    /// an Approve verdict; false for both a denial and a revision. Kept for clients written
    /// before <see cref="Verdict"/> existed.
    /// </summary>
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    /// <summary>
    /// The approver's verdict ("Deny", "Approve", or "Revise"). Optional so this event's shape
    /// stays additive for any client that predates it.
    /// </summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    /// <summary>Optional reason for the decision.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// The reviewer's steering instructions, present when <see cref="Verdict"/> is "Revise".
    /// Without this a dashboard client subscribed only to the push channel could see that a
    /// revision was requested but never the reviewer's actual words — the one piece of data the
    /// Revise verdict exists to carry.
    /// </summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }
}

/// <summary>
/// Warns that a pending escalation is approaching its timeout deadline.
/// Enables the dashboard to display a countdown or urgency indicator.
/// </summary>
public sealed record EscalationExpiringEvent : AgUiEvent
{
    /// <summary>Correlates back to the originating escalation request.</summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }

    /// <summary>Seconds remaining before the escalation times out.</summary>
    [JsonPropertyName("remainingSeconds")]
    public required int RemainingSeconds { get; init; }
}

/// <summary>
/// Reports what happened when an approved escalation's action was actually carried out —
/// closes the approval loop so a failed action and a completed one no longer look identical to
/// the approver.
/// </summary>
/// <remarks>
/// Emitted inline within the same turn for a tool-call approval, so this reaches the UI live.
/// For a plan-executor approval the plan resumes with no AG-UI run active, so this event reaches
/// only the audit trail — the loop still closes, just not on this surface. See
/// <c>AgUiEscalationNotifier</c>.
/// </remarks>
public sealed record EscalationExecutedEvent : AgUiEvent
{
    /// <summary>Correlates back to the originating escalation.</summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }

    /// <summary>Whether the action succeeded, failed, or never ran (e.g. "Succeeded", "Failed", "NeverExecuted").</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Why the action failed. Present only when <see cref="Status"/> is "Failed".</summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }

    /// <summary>Why the action never ran. Present only when <see cref="Status"/> is "NeverExecuted".</summary>
    [JsonPropertyName("notExecutedReason")]
    public string? NotExecutedReason { get; init; }

    /// <summary>When this report was produced.</summary>
    [JsonPropertyName("reportedAt")]
    public required DateTimeOffset ReportedAt { get; init; }

    /// <summary>A stable identifier for the site that produced this report (e.g. "plan-executor").</summary>
    [JsonPropertyName("reportedBy")]
    public required string ReportedBy { get; init; }
}
