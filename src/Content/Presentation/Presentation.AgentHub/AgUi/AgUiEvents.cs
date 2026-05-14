using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Base type for all AG-UI protocol events serialized as SSE <c>data:</c> frames.
/// <para>
/// The <c>type</c> property serves as the polymorphic discriminator on the wire.
/// Callers must serialize against this base type so that <see cref="JsonPolymorphicAttribute"/>
/// emits the correct discriminator for each derived event. Serializing a derived type
/// directly bypasses polymorphism and omits the discriminator.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartedEvent), AgUiEventType.RunStarted)]
[JsonDerivedType(typeof(RunFinishedEvent), AgUiEventType.RunFinished)]
[JsonDerivedType(typeof(RunErrorEvent), AgUiEventType.RunError)]
[JsonDerivedType(typeof(TextMessageStartEvent), AgUiEventType.TextMessageStart)]
[JsonDerivedType(typeof(TextMessageContentEvent), AgUiEventType.TextMessageContent)]
[JsonDerivedType(typeof(TextMessageEndEvent), AgUiEventType.TextMessageEnd)]
[JsonDerivedType(typeof(EscalationRequestedEvent), AgUiEventType.EscalationRequested)]
[JsonDerivedType(typeof(EscalationResolvedEvent), AgUiEventType.EscalationResolved)]
[JsonDerivedType(typeof(EscalationExpiringEvent), AgUiEventType.EscalationExpiring)]
public abstract record AgUiEvent;

/// <summary>
/// Signals the start of an agent run. Emitted once at the beginning of every run
/// before any messages or tool calls are streamed.
/// </summary>
public sealed record RunStartedEvent(
    /// <summary>The conversation thread that owns this run.</summary>
    [property: JsonPropertyName("threadId")] string ThreadId,
    /// <summary>Unique identifier for this run, echoed in <see cref="RunFinishedEvent"/>.</summary>
    [property: JsonPropertyName("runId")] string RunId
) : AgUiEvent;

/// <summary>
/// Signals successful completion of an agent run. Always paired with a preceding
/// <see cref="RunStartedEvent"/> carrying the same <see cref="ThreadId"/> and <see cref="RunId"/>.
/// </summary>
public sealed record RunFinishedEvent(
    /// <summary>The conversation thread that owns this run.</summary>
    [property: JsonPropertyName("threadId")] string ThreadId,
    /// <summary>Unique identifier for the run that has completed.</summary>
    [property: JsonPropertyName("runId")] string RunId
) : AgUiEvent;

/// <summary>
/// Signals a fatal error during an agent run. The run is considered terminated
/// after this event; no <see cref="RunFinishedEvent"/> will follow.
/// </summary>
public sealed record RunErrorEvent(
    /// <summary>Human-readable description of the error.</summary>
    [property: JsonPropertyName("message")] string Message
) : AgUiEvent;

/// <summary>
/// Signals the start of a new text message being streamed from the agent.
/// Followed by one or more <see cref="TextMessageContentEvent"/> frames and
/// terminated by a <see cref="TextMessageEndEvent"/>.
/// </summary>
public sealed record TextMessageStartEvent(
    /// <summary>Unique identifier for this message, stable across all its delta frames.</summary>
    [property: JsonPropertyName("messageId")] string MessageId,
    /// <summary>Message role (e.g. <c>assistant</c>, <c>tool</c>).</summary>
    [property: JsonPropertyName("role")] string Role
) : AgUiEvent;

/// <summary>
/// A streaming text chunk (delta) within an in-progress message.
/// Multiple content frames may arrive for a single message before
/// the corresponding <see cref="TextMessageEndEvent"/>.
/// </summary>
public sealed record TextMessageContentEvent(
    /// <summary>The message this chunk belongs to.</summary>
    [property: JsonPropertyName("messageId")] string MessageId,
    /// <summary>The incremental text to append to the message buffer.</summary>
    [property: JsonPropertyName("delta")] string Delta
) : AgUiEvent;

/// <summary>
/// Signals the end of a text message. The full message content can be assembled
/// by concatenating all preceding <see cref="TextMessageContentEvent.Delta"/> values
/// for the same <see cref="MessageId"/>.
/// </summary>
public sealed record TextMessageEndEvent(
    /// <summary>The message that has finished streaming.</summary>
    [property: JsonPropertyName("messageId")] string MessageId
) : AgUiEvent;

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
