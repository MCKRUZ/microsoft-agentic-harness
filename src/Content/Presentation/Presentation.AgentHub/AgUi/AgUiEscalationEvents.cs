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

    /// <summary>Whether the approver granted approval.</summary>
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    /// <summary>Optional reason for the decision.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
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
