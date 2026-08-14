using Application.AI.Common.Interfaces;

namespace Application.AI.Common.Services;

/// <summary>
/// Ambient holder plus delegate-backed implementation of <see cref="IAgentTurnStreamSink"/>.
/// Mirrors the <see cref="LlmUsageCapture.Current"/> <see cref="AsyncLocal{T}"/> pattern: the
/// transport (orchestrator) sets <see cref="Current"/> to a sink wrapping its per-chunk
/// callback before dispatching the turn, and the handler reads <see cref="Current"/> to choose
/// streaming over blocking execution. Flowing the sink ambiently keeps the MediatR command a
/// pure data record (no delegate that would break value-equality or cache keys).
/// </summary>
public sealed class AgentTurnStreamSink : IAgentTurnStreamSink
{
    private static readonly AsyncLocal<IAgentTurnStreamSink?> s_current = new();

    /// <summary>
    /// The sink attached to the current async flow, or <c>null</c> when the turn has no live
    /// consumer (tests, batch jobs). Set by the transport before dispatch; cleared afterward.
    /// </summary>
    public static IAgentTurnStreamSink? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    private readonly Func<string, CancellationToken, Task> _onDelta;
    private readonly Func<string, string, string, CancellationToken, Task>? _onToolCall;
    private readonly Func<string, string, CancellationToken, Task>? _onToolCallResult;

    /// <summary>
    /// Creates a sink that forwards each assistant text delta to <paramref name="onDelta"/>, each
    /// complete tool call to <paramref name="onToolCall"/>, and each tool result to
    /// <paramref name="onToolCallResult"/>. A transport that only cares about text (e.g.
    /// <c>ConversationOrchestrator</c>'s SignalR path) can omit the tool-call callbacks entirely; the
    /// corresponding <see cref="IAgentTurnStreamSink"/> methods then no-op, identical to this type's
    /// behaviour before tool-call streaming existed.
    /// </summary>
    /// <param name="onDelta">The transport callback invoked per text delta.</param>
    /// <param name="onToolCall">Invoked with a complete tool call (id, name, arguments), or <see langword="null"/> to ignore it.</param>
    /// <param name="onToolCallResult">Invoked with a tool call's result, or <see langword="null"/> to ignore it.</param>
    public AgentTurnStreamSink(
        Func<string, CancellationToken, Task> onDelta,
        Func<string, string, string, CancellationToken, Task>? onToolCall = null,
        Func<string, string, CancellationToken, Task>? onToolCallResult = null)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        _onDelta = onDelta;
        _onToolCall = onToolCall;
        _onToolCallResult = onToolCallResult;
    }

    /// <inheritdoc />
    public Task EmitAsync(string delta, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(delta) ? Task.CompletedTask : _onDelta(delta, cancellationToken);

    /// <inheritdoc />
    public Task EmitToolCallAsync(string toolCallId, string toolCallName, string argsJson, CancellationToken cancellationToken) =>
        _onToolCall?.Invoke(toolCallId, toolCallName, argsJson, cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task EmitToolCallResultAsync(string toolCallId, string result, CancellationToken cancellationToken) =>
        _onToolCallResult?.Invoke(toolCallId, result, cancellationToken) ?? Task.CompletedTask;
}
