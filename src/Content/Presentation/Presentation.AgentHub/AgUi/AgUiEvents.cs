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
[JsonDerivedType(typeof(DriftWarnEvent), AgUiEventType.DriftWarn)]
[JsonDerivedType(typeof(DriftAlertEvent), AgUiEventType.DriftAlert)]
[JsonDerivedType(typeof(DriftEscalateEvent), AgUiEventType.DriftEscalate)]
[JsonDerivedType(typeof(DriftResolvedEvent), AgUiEventType.DriftResolved)]
[JsonDerivedType(typeof(LearningCapturedEvent), AgUiEventType.LearningCaptured)]
[JsonDerivedType(typeof(LearningAppliedEvent), AgUiEventType.LearningApplied)]
[JsonDerivedType(typeof(LearningForgottenEvent), AgUiEventType.LearningForgotten)]
[JsonDerivedType(typeof(PlanStartedEvent), AgUiEventType.PlanStarted)]
[JsonDerivedType(typeof(PlanStepStartedEvent), AgUiEventType.PlanStepStarted)]
[JsonDerivedType(typeof(PlanStepCompletedEvent), AgUiEventType.PlanStepCompleted)]
[JsonDerivedType(typeof(PlanStateUpdateEvent), AgUiEventType.PlanStateDelta)]
[JsonDerivedType(typeof(SandboxStatusEvent), AgUiEventType.SandboxStatus)]
[JsonDerivedType(typeof(PlanCompletedEvent), AgUiEventType.PlanCompleted)]
[JsonDerivedType(typeof(PlanFailedEvent), AgUiEventType.PlanFailed)]
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

/// <summary>
/// Signals that quality drift was detected at warning severity.
/// The agent's output quality has deviated from baseline but not critically.
/// </summary>
public sealed record DriftWarnEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured (e.g. "Agent", "Skill").</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
}

/// <summary>
/// Signals that quality drift was detected at alert severity.
/// Includes the baseline ID for correlation with baseline store records.
/// </summary>
public sealed record DriftAlertEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }
}

/// <summary>
/// Signals that quality drift was detected at escalation severity.
/// An escalation request has been triggered and is awaiting human review.
/// </summary>
public sealed record DriftEscalateEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }

    /// <summary>
    /// Escalation correlation ID. Empty when the escalation has not yet been queued;
    /// clients correlate drift-escalate events with escalation-requested events by timestamp and scope.
    /// </summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }
}

/// <summary>
/// Signals that a previously detected drift has been resolved through
/// learning application, baseline adjustment, manual dismissal, or escalation resolution.
/// </summary>
public sealed record DriftResolvedEvent : AgUiEvent
{
    /// <summary>The drift event that was resolved.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary>How the drift was resolved (e.g. "LearningApplied", "BaselineAdjusted").</summary>
    [JsonPropertyName("resolutionType")]
    public required string ResolutionType { get; init; }

    /// <summary>Identifier of the resolving entity (learning ID, escalation ID, etc.).</summary>
    [JsonPropertyName("resolvedBy")]
    public required string ResolvedBy { get; init; }

    /// <summary>When the drift was resolved.</summary>
    [JsonPropertyName("resolvedAt")]
    public required DateTimeOffset ResolvedAt { get; init; }
}

/// <summary>
/// Signals that the agent has captured a new learning. Emitted after a
/// <c>RememberCommand</c> successfully persists a learning entry.
/// </summary>
public sealed record LearningCapturedEvent : AgUiEvent
{
    /// <summary>Unique identifier for the learning entry.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Learning category (e.g. "FactualCorrection", "StylePreference").</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Agent ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    /// <summary>Team ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("teamId")]
    public string? TeamId { get; init; }

    /// <summary>Whether this is a global learning.</summary>
    [JsonPropertyName("isGlobal")]
    public required bool IsGlobal { get; init; }

    /// <summary>Human-readable description of the learning source.</summary>
    [JsonPropertyName("sourceDescription")]
    public required string SourceDescription { get; init; }
}

/// <summary>
/// Signals that a previously captured learning was applied during agent execution.
/// The learning's content influenced the agent's response or tool usage.
/// </summary>
public sealed record LearningAppliedEvent : AgUiEvent
{
    /// <summary>The learning that was applied.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>The agent that applied the learning.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>Learning category for display.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Optional summary of the context in which the learning was applied.</summary>
    [JsonPropertyName("contextSummary")]
    public string? ContextSummary { get; init; }
}

/// <summary>
/// Signals that a learning has been forgotten (soft-deleted) and will no longer
/// influence future agent behavior.
/// </summary>
public sealed record LearningForgottenEvent : AgUiEvent
{
    /// <summary>The learning that was forgotten.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Reason for forgetting this learning.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>
/// Signals that a plan has started executing.
/// </summary>
public sealed record PlanStartedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Human-readable plan name.</summary>
    [JsonPropertyName("planName")]
    public required string PlanName { get; init; }

    /// <summary>Total number of steps in the plan graph.</summary>
    [JsonPropertyName("totalSteps")]
    public required int TotalSteps { get; init; }
}

/// <summary>
/// Signals that a plan step has started executing.
/// </summary>
public sealed record PlanStepStartedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Human-readable step name.</summary>
    [JsonPropertyName("stepName")]
    public required string StepName { get; init; }

    /// <summary>The step's execution type (e.g. "LlmCall", "ToolUse").</summary>
    [JsonPropertyName("stepType")]
    public required string StepType { get; init; }
}

/// <summary>
/// Signals that a plan step has completed (successfully, failed, or skipped).
/// </summary>
public sealed record PlanStepCompletedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Final status of the step (e.g. "Completed", "Failed").</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    /// <summary>Brief summary of step output. Null if no output.</summary>
    [JsonPropertyName("outputSummary")]
    public string? OutputSummary { get; init; }
}

/// <summary>
/// An incremental JSON-Patch delta applied to plan step state.
/// Uses RFC 6902 patch operations to encode step status transitions.
/// </summary>
public sealed record PlanStateUpdateEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>RFC 6902 JSON Patch operations.</summary>
    [JsonPropertyName("patch")]
    public required IReadOnlyList<JsonPatchOperation> Patch { get; init; }
}

/// <summary>
/// A single RFC 6902 JSON Patch operation.
/// </summary>
public sealed record JsonPatchOperation
{
    /// <summary>Patch operation type (e.g. "replace", "add", "remove").</summary>
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    /// <summary>JSON Pointer path to the target value.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The value to apply at the target path.</summary>
    [JsonPropertyName("value")]
    public required object Value { get; init; }
}

/// <summary>
/// Reports sandbox resource usage and attestation for a tool execution step.
/// </summary>
public sealed record SandboxStatusEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>Name of the tool being executed.</summary>
    [JsonPropertyName("toolName")]
    public required string ToolName { get; init; }

    /// <summary>Sandbox isolation level (e.g. "None", "Process", "Container").</summary>
    [JsonPropertyName("isolationLevel")]
    public required string IsolationLevel { get; init; }

    /// <summary>Memory consumed in bytes.</summary>
    [JsonPropertyName("memoryUsedBytes")]
    public required long MemoryUsedBytes { get; init; }

    /// <summary>CPU time consumed in milliseconds.</summary>
    [JsonPropertyName("cpuTimeMs")]
    public required long CpuTimeMs { get; init; }

    /// <summary>HMAC attestation hash. Null if attestation unavailable.</summary>
    [JsonPropertyName("attestationHash")]
    public string? AttestationHash { get; init; }
}

/// <summary>
/// Signals that an entire plan completed successfully.
/// </summary>
public sealed record PlanCompletedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    [JsonPropertyName("totalDurationMs")]
    public required long TotalDurationMs { get; init; }
}

/// <summary>
/// Signals that a plan failed due to a step failure.
/// </summary>
public sealed record PlanFailedEvent : AgUiEvent
{
    /// <summary>Identifier of the plan.</summary>
    [JsonPropertyName("planId")]
    public required string PlanId { get; init; }

    /// <summary>Identifier of the step that caused the failure.</summary>
    [JsonPropertyName("failedStepId")]
    public required string FailedStepId { get; init; }

    /// <summary>Error message from the failed step.</summary>
    [JsonPropertyName("errorMessage")]
    public required string ErrorMessage { get; init; }
}
