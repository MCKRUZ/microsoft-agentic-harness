namespace Presentation.AgentHub.AgUi;

/// <summary>
/// AG-UI protocol event type discriminators.
/// Values match the AG-UI specification wire format exactly — uppercase with underscores.
/// These constants are used in <see cref="AgUiEvent"/> polymorphic type attributes and
/// serialized as the <c>type</c> field in SSE <c>data:</c> frames.
/// </summary>
public static class AgUiEventType
{
    /// <summary>Signals the start of an agent run.</summary>
    public const string RunStarted = "RUN_STARTED";

    /// <summary>Signals successful completion of an agent run.</summary>
    public const string RunFinished = "RUN_FINISHED";

    /// <summary>Signals a fatal error during an agent run.</summary>
    public const string RunError = "RUN_ERROR";

    /// <summary>Signals the start of a named execution step within a run.</summary>
    public const string StepStarted = "STEP_STARTED";

    /// <summary>Signals the completion of a named execution step.</summary>
    public const string StepFinished = "STEP_FINISHED";

    /// <summary>Signals the start of a new text message from the agent.</summary>
    public const string TextMessageStart = "TEXT_MESSAGE_START";

    /// <summary>A streaming text chunk (delta) within an in-progress message.</summary>
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";

    /// <summary>Signals the end of a text message.</summary>
    public const string TextMessageEnd = "TEXT_MESSAGE_END";

    /// <summary>Signals that a tool call has begun.</summary>
    public const string ToolCallStart = "TOOL_CALL_START";

    /// <summary>A streaming arguments chunk for an in-progress tool call.</summary>
    public const string ToolCallArgs = "TOOL_CALL_ARGS";

    /// <summary>Signals that a tool call has finished (arguments fully streamed).</summary>
    public const string ToolCallEnd = "TOOL_CALL_END";

    /// <summary>Carries the result returned by a completed tool call.</summary>
    public const string ToolCallResult = "TOOL_CALL_RESULT";

    /// <summary>A full snapshot of the agent's state at a point in time.</summary>
    public const string StateSnapshot = "STATE_SNAPSHOT";

    /// <summary>An incremental JSON-Patch delta applied to the agent's state.</summary>
    public const string StateDelta = "STATE_DELTA";

    /// <summary>A full snapshot of the conversation message list.</summary>
    public const string MessagesSnapshot = "MESSAGES_SNAPSHOT";

    /// <summary>A raw, passthrough event not mapped to a typed AG-UI event.</summary>
    public const string Raw = "RAW";

    /// <summary>A custom, application-defined event type.</summary>
    public const string Custom = "CUSTOM";

    /// <summary>Signals that an agent action requires human approval.</summary>
    public const string EscalationRequested = "ESCALATION_REQUESTED";

    /// <summary>Signals that a pending escalation has been resolved.</summary>
    public const string EscalationResolved = "ESCALATION_RESOLVED";

    /// <summary>Warns that a pending escalation is approaching its timeout deadline.</summary>
    public const string EscalationExpiring = "ESCALATION_EXPIRING";

    /// <summary>Signals that drift was detected at warn severity.</summary>
    public const string DriftWarn = "DRIFT_WARN";

    /// <summary>Signals that drift was detected at alert severity.</summary>
    public const string DriftAlert = "DRIFT_ALERT";

    /// <summary>Signals that drift was detected at escalate severity.</summary>
    public const string DriftEscalate = "DRIFT_ESCALATE";

    /// <summary>Signals that a previously detected drift has been resolved.</summary>
    public const string DriftResolved = "DRIFT_RESOLVED";

    /// <summary>Signals that a new learning has been captured.</summary>
    public const string LearningCaptured = "LEARNING_CAPTURED";

    /// <summary>Signals that a learning was applied during agent execution.</summary>
    public const string LearningApplied = "LEARNING_APPLIED";

    /// <summary>Signals that a learning has been forgotten (soft-deleted).</summary>
    public const string LearningForgotten = "LEARNING_FORGOTTEN";
}
