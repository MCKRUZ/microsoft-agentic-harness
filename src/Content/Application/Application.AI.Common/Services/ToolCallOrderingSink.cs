using Application.AI.Common.Interfaces;

namespace Application.AI.Common.Services;

/// <summary>
/// Per-turn decorator around an <see cref="IAgentTurnStreamSink"/> that enforces the tool-call
/// ordering invariant structurally, for every caller of the wrapped sink: a
/// <see cref="EmitToolCallResultAsync"/> call is silently dropped unless a matching
/// <see cref="EmitToolCallAsync"/> for the same <c>toolCallId</c> already streamed through this
/// instance, and a duplicate <see cref="EmitToolCallAsync"/> for an id already started is silently
/// dropped rather than double-emitted (guards a provider connector surfacing the same call twice).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Must be constructed fresh for every turn — never reused across turns, and never held as a
/// long-lived instance.</strong> The bundle SSE path arms one ambient <see cref="AgentTurnStreamSink"/>
/// for an entire (potentially multi-turn) run (see <c>BundleRunStreamer</c>), but some provider
/// connectors number tool-call ids per-turn (e.g. <c>call_0</c>, <c>call_1</c>, resetting each turn).
/// If this decorator's started-id set lived for the whole run instead of one turn, turn 2's
/// <c>call_0</c> would look like a duplicate of turn 1's — silently dropping its
/// <c>TOOL_CALL_START</c> while its result still streamed, reproducing the exact orphaned-result bug
/// this type exists to prevent. <c>ExecuteAgentTurnCommandHandler.RunStreamingTurnAsync</c> (in
/// <c>Application.Core</c>) constructs a new instance inside each turn's streaming loop for exactly
/// this reason.
/// </para>
/// <para>
/// <see cref="EmitToolCallAsync"/> records the id <em>before</em> delegating to the inner sink, so a
/// transport that only supplied an <c>onToolCallResult</c> callback (and left <c>onToolCall</c> as a
/// no-op) still correlates correctly — registration does not depend on the inner sink actually doing
/// anything with the call.
/// </para>
/// <para>
/// Not thread-safe by design, not by oversight: a turn's streaming loop processes updates from one
/// <c>await foreach</c> sequentially, so the one instance wrapping it is never called concurrently.
/// </para>
/// </remarks>
public sealed class ToolCallOrderingSink : IAgentTurnStreamSink
{
    private readonly IAgentTurnStreamSink _inner;
    private readonly HashSet<string> _startedCallIds = new(StringComparer.Ordinal);

    /// <summary>Wraps <paramref name="inner"/>, enforcing tool-call ordering on every call through this instance.</summary>
    public ToolCallOrderingSink(IAgentTurnStreamSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public Task EmitAsync(string delta, CancellationToken cancellationToken) =>
        _inner.EmitAsync(delta, cancellationToken);

    /// <inheritdoc />
    public Task EmitToolCallAsync(string toolCallId, string toolCallName, StreamedToolCallArguments args, CancellationToken cancellationToken)
    {
        if (!_startedCallIds.Add(toolCallId))
            return Task.CompletedTask;

        return _inner.EmitToolCallAsync(toolCallId, toolCallName, args, cancellationToken);
    }

    /// <inheritdoc />
    public Task EmitToolCallResultAsync(string toolCallId, string result, CancellationToken cancellationToken)
    {
        if (!_startedCallIds.Contains(toolCallId))
            return Task.CompletedTask;

        return _inner.EmitToolCallResultAsync(toolCallId, result, cancellationToken);
    }
}
