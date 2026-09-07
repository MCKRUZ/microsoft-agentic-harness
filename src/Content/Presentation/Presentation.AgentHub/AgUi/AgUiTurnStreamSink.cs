using Application.AI.Common.Interfaces;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// The <see cref="IAgentTurnStreamSink"/> an AG-UI run attaches for the duration of its dispatch,
/// translating real text deltas and server-executed tool-call activity into AG-UI SSE events on
/// that run's own <see cref="IAgUiEventWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that was missing for AG-UI to deliver on the reason it was chosen over the
/// SignalR hub: <c>ConversationOrchestrator</c> (SignalR) already attaches an
/// <c>AgentTurnStreamSink</c> for text but passes <c>null</c> for the tool-call callbacks, so it has
/// always dropped tool visibility — the capability was never missing, only unused there. This type
/// is the AG-UI transport's equivalent attachment, populating all three callbacks.
/// </para>
/// <para>
/// Tool-call announcements reuse the same <see cref="ToolCallStartEvent"/>/<see cref="ToolCallArgsEvent"/>/
/// <see cref="ToolCallEndEvent"/> sequence <c>AgUiClientToolBridge</c> uses for its own, unrelated
/// mid-run client-round-trip case — the wire shape is identical; only the follow-up differs (a
/// server-executed tool's actual output arrives here as <see cref="ToolCallResultEvent"/>, whereas a
/// client-round-trip tool's result arrives out-of-band via <c>POST /ag-ui/tool-result</c>).
/// </para>
/// <para>
/// <strong><see cref="Started"/> exists because TEXT_MESSAGE_START must never be sent for a turn
/// that never produces a message.</strong> A turn that fails before any generation happens (a
/// configuration error, a MediatR exception) must emit only RUN_ERROR — no orphaned, empty text
/// message frame. This sink therefore starts the message lazily, on its first real delta, rather
/// than the caller starting it unconditionally before dispatch; <see cref="Started"/> tells
/// <c>AgUiRunHandler</c> whether that ever happened, so it knows whether to fall back to emitting
/// the complete response as one frame (the pre-streaming behavior, still correct for any caller —
/// including tests — that never drives this sink in real time) or whether the message was already
/// framed as it streamed.
/// </para>
/// </remarks>
internal sealed class AgUiTurnStreamSink : IAgentTurnStreamSink
{
    private readonly IAgUiEventWriter _writer;
    private readonly string _messageId;
    private bool _started;

    /// <summary>Creates a sink writing events for the message <paramref name="messageId"/> onto <paramref name="writer"/>.</summary>
    public AgUiTurnStreamSink(IAgUiEventWriter writer, string messageId)
    {
        _writer = writer;
        _messageId = messageId;
    }

    /// <summary>
    /// Whether this sink has emitted TEXT_MESSAGE_START — i.e. whether at least one real delta
    /// streamed through it. The caller must emit TEXT_MESSAGE_END if and only if this is true by the
    /// time it decides how to close out the message.
    /// </summary>
    public bool Started => _started;

    /// <inheritdoc />
    public async Task EmitAsync(string delta, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        if (!_started)
        {
            _started = true;
            await _writer.WriteAsync(new TextMessageStartEvent(_messageId, "assistant"), cancellationToken);
        }

        await _writer.WriteAsync(new TextMessageContentEvent(_messageId, delta), cancellationToken);
    }

    /// <inheritdoc />
    public async Task EmitToolCallAsync(
        string toolCallId, string toolCallName, StreamedToolCallArguments args, CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(new ToolCallStartEvent(toolCallId, toolCallName), cancellationToken);
        await _writer.WriteAsync(
            new ToolCallArgsEvent(toolCallId, args.Json, args.Withheld ? true : null), cancellationToken);
        await _writer.WriteAsync(new ToolCallEndEvent(toolCallId), cancellationToken);
    }

    /// <inheritdoc />
    public Task EmitToolCallResultAsync(
        string toolCallId, StreamedToolCallResult result, CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new ToolCallResultEvent(toolCallId, result.Text, result.Withheld ? true : null), cancellationToken);
}
