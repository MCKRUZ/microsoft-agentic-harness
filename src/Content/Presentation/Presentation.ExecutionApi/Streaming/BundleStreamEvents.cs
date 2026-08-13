using System.Text.Json.Serialization;

namespace Presentation.ExecutionApi.Streaming;

/// <summary>
/// Base type for the events the bundle streaming endpoint writes as Server-Sent-Events <c>data:</c> frames.
/// </summary>
/// <remarks>
/// <para>
/// These records reproduce the subset of the AG-UI protocol a bundle run needs — run lifecycle plus assistant
/// text streaming — so any AG-UI client library consumes the feed unchanged. The bundle API deliberately ships
/// its <em>own</em> small copy rather than referencing the dashboard's full AG-UI vocabulary: this is a lean,
/// isolated host for externally-authored agents, and coupling it to the dashboard's 25-event protocol (plan,
/// drift, learning, escalation events it will never emit) would defeat that isolation. If a third host ever
/// needs this, extract a shared Server-Sent-Events primitive then.
/// </para>
/// <para>
/// The <c>type</c> property is the polymorphic discriminator on the wire. Frames MUST be serialized against
/// this base type so <see cref="JsonPolymorphicAttribute"/> emits the discriminator; serializing a derived
/// type directly omits it. The discriminator values match the AG-UI wire spec exactly (uppercase, underscores).
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BundleRunStartedEvent), "RUN_STARTED")]
[JsonDerivedType(typeof(BundleTextMessageStartEvent), "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(BundleTextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(BundleTextMessageEndEvent), "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(BundleToolCallStartEvent), "TOOL_CALL_START")]
[JsonDerivedType(typeof(BundleToolCallArgsEvent), "TOOL_CALL_ARGS")]
[JsonDerivedType(typeof(BundleToolCallEndEvent), "TOOL_CALL_END")]
[JsonDerivedType(typeof(BundleToolCallResultEvent), "TOOL_CALL_RESULT")]
[JsonDerivedType(typeof(BundleRunFinishedEvent), "RUN_FINISHED")]
[JsonDerivedType(typeof(BundleRunErrorEvent), "RUN_ERROR")]
public abstract record BundleStreamEvent;

/// <summary>Emitted once at the start of a streamed run, before any text.</summary>
public sealed record BundleRunStartedEvent(
    /// <summary>Thread the run belongs to; the bundle handle, stable across the run.</summary>
    [property: JsonPropertyName("threadId")] string ThreadId,
    /// <summary>Unique id for this run (the job id), echoed in the terminal event.</summary>
    [property: JsonPropertyName("runId")] string RunId) : BundleStreamEvent;

/// <summary>Signals the start of the assistant message. Followed by content deltas and one end frame.</summary>
public sealed record BundleTextMessageStartEvent(
    /// <summary>Id of the message, stable across all of its delta frames.</summary>
    [property: JsonPropertyName("messageId")] string MessageId,
    /// <summary>Message role — always <c>assistant</c> for a bundle run.</summary>
    [property: JsonPropertyName("role")] string Role) : BundleStreamEvent;

/// <summary>A streaming text delta to append to the in-progress assistant message.</summary>
public sealed record BundleTextMessageContentEvent(
    /// <summary>The message this delta belongs to.</summary>
    [property: JsonPropertyName("messageId")] string MessageId,
    /// <summary>The incremental text to append to the message buffer.</summary>
    [property: JsonPropertyName("delta")] string Delta) : BundleStreamEvent;

/// <summary>Signals the end of the assistant message.</summary>
public sealed record BundleTextMessageEndEvent(
    /// <summary>The message that has finished streaming.</summary>
    [property: JsonPropertyName("messageId")] string MessageId) : BundleStreamEvent;

/// <summary>
/// Emitted when the agent decides to call a tool. Field names match the AgentHub's own AG-UI
/// tool-call vocabulary (<c>Presentation.AgentHub/AgUi/AgUiToolCallEvents.cs</c>) for consistency,
/// even though this file deliberately does not reference those types directly — see this file's
/// top-level remarks.
/// </summary>
public sealed record BundleToolCallStartEvent(
    /// <summary>The provider-assigned id for this call, shared by every event for it.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The name of the tool being called.</summary>
    [property: JsonPropertyName("toolCallName")] string ToolCallName) : BundleStreamEvent;

/// <summary>
/// Carries a tool call's arguments. Unlike <see cref="BundleTextMessageContentEvent"/>, this is NOT an
/// incremental delta — the underlying chat-client abstraction only ever exposes a tool call's
/// arguments once fully assembled, so <see cref="Delta"/> always carries the complete JSON payload in
/// one frame, immediately followed by <see cref="BundleToolCallEndEvent"/>.
/// </summary>
public sealed record BundleToolCallArgsEvent(
    /// <summary>The call this payload belongs to.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The tool call's complete arguments, serialized as JSON.</summary>
    [property: JsonPropertyName("delta")] string Delta) : BundleStreamEvent;

/// <summary>Signals that a tool call's arguments are complete.</summary>
public sealed record BundleToolCallEndEvent(
    /// <summary>The call that has finished emitting its arguments.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId) : BundleStreamEvent;

/// <summary>
/// Emitted once a tool call has actually run and produced a result. No precedent field-naming to
/// match — <c>AgUiEventType.ToolCallResult</c> is a dead wire-vocabulary constant with no backing
/// record anywhere in the repo.
/// </summary>
public sealed record BundleToolCallResultEvent(
    /// <summary>The call this result belongs to.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The tool's output.</summary>
    [property: JsonPropertyName("result")] string Result) : BundleStreamEvent;

/// <summary>Emitted once on successful completion of a streamed run. Terminal; no further frames follow.</summary>
public sealed record BundleRunFinishedEvent(
    /// <summary>Thread the completed run belonged to.</summary>
    [property: JsonPropertyName("threadId")] string ThreadId,
    /// <summary>Id of the run that has completed.</summary>
    [property: JsonPropertyName("runId")] string RunId) : BundleStreamEvent;

/// <summary>
/// Emitted once when a streamed run fails. Terminal; no <see cref="BundleRunFinishedEvent"/> follows. The
/// message is always a caller-safe reason — never a raw exception (the executor logs and scrubs those).
/// </summary>
public sealed record BundleRunErrorEvent(
    /// <summary>A caller-safe, human-readable description of why the run failed.</summary>
    [property: JsonPropertyName("message")] string Message) : BundleStreamEvent;
