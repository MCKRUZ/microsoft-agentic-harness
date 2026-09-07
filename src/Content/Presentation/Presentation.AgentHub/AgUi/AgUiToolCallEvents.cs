using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Signals that the agent has begun a tool call whose execution is delegated to the
/// connected client (a "client-side" or round-trip tool). Followed by one or more
/// <see cref="ToolCallArgsEvent"/> frames carrying the serialized arguments and
/// terminated by a <see cref="ToolCallEndEvent"/>.
/// </summary>
/// <remarks>
/// Unlike the standard AG-UI client-tool flow (which terminates the run and expects the
/// client to start a follow-up run), these events are emitted <em>mid-run</em> by the
/// server-side blocking proxy tool. The server pauses the same run awaiting the client's
/// result via <c>POST /ag-ui/tool-result</c>, then resumes. The <see cref="ToolCallId"/>
/// is globally unique per run and is echoed by the client when it posts the result.
/// </remarks>
public sealed record ToolCallStartEvent(
    /// <summary>Globally-unique identifier for this tool call, echoed by the client's result post.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The name of the tool being invoked (e.g. <c>dashboard_control</c>).</summary>
    [property: JsonPropertyName("toolCallName")] string ToolCallName
) : AgUiEvent;

/// <summary>
/// A streaming arguments chunk (delta) for an in-progress tool call. The full argument
/// payload is assembled by concatenating all <see cref="Delta"/> values for the same
/// <see cref="ToolCallId"/>. The blocking proxy emits the arguments as a single JSON delta —
/// unless <see cref="Withheld"/> is true, in which case <see cref="Delta"/> is the fixed
/// placeholder <c>"{}"</c> because the real arguments exceeded the streaming size ceiling.
/// </summary>
public sealed record ToolCallArgsEvent(
    /// <summary>The tool call these arguments belong to.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The incremental arguments text (JSON) to append to the call's argument buffer, or <c>"{}"</c> if withheld.</summary>
    [property: JsonPropertyName("delta")] string Delta,
    /// <summary>
    /// <see langword="true"/> when the real arguments were withheld for exceeding the size ceiling.
    /// Nullable, and only ever constructed as <see langword="true"/> or <see langword="null"/> — never
    /// <see langword="false"/> — so the field is omitted from the wire on every normal frame.
    /// </summary>
    [property: JsonPropertyName("withheld")] bool? Withheld = null
) : AgUiEvent;

/// <summary>
/// Signals that a tool call's arguments have been fully streamed. After this frame the
/// client should execute the requested action and post the result back to
/// <c>POST /ag-ui/tool-result</c> with the matching <see cref="ToolCallId"/>.
/// </summary>
public sealed record ToolCallEndEvent(
    /// <summary>The tool call that has finished streaming arguments.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId
) : AgUiEvent;

/// <summary>
/// The result of a tool call the server executed itself (a normal MCP or local tool, as opposed to
/// the mid-run client-round-trip flow the three events above exist for — that case's result arrives
/// via the browser's own <c>POST /ag-ui/tool-result</c>, never as an event). Emitted once, after the
/// matching <see cref="ToolCallEndEvent"/>, by a turn's attached streaming sink.
/// </summary>
public sealed record ToolCallResultEvent(
    /// <summary>The tool call this result belongs to.</summary>
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    /// <summary>The tool's redacted output text, or empty when <see cref="Withheld"/> is true.</summary>
    [property: JsonPropertyName("result")] string Result,
    /// <summary>
    /// <see langword="true"/> when the real result was withheld (oversized or unredactable) — see
    /// <c>StreamedToolCallResult.Withheld</c> for the full contract. Never <see langword="false"/>,
    /// so omitted from the wire on every normal frame.
    /// </summary>
    [property: JsonPropertyName("withheld")] bool? Withheld = null
) : AgUiEvent;
