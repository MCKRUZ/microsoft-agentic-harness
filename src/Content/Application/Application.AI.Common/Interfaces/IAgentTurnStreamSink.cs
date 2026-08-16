namespace Application.AI.Common.Interfaces;

/// <summary>
/// Ambient seam that lets the agent-turn handler push assistant text deltas to the
/// active transport (e.g. a SignalR connection) as the model generates them, without
/// the Application layer taking a dependency on any transport.
/// </summary>
/// <remarks>
/// <para>
/// The transport (the Presentation-layer orchestrator) attaches a sink before
/// dispatching the turn; the handler reads the ambient sink to decide between
/// streaming (<c>RunStreamingAsync</c>) and blocking (<c>RunAsync</c>) execution.
/// When no sink is attached the handler runs blocking, so non-interactive callers
/// (tests, batch jobs) keep their existing behaviour. Mirrors the ambient pattern of
/// <see cref="Services.LlmUsageCapture.Current"/>.
/// </para>
/// <para>
/// <strong>Ordering contract for tool-call activity:</strong> a caller must never invoke
/// <see cref="EmitToolCallResultAsync"/> for a <c>toolCallId</c> it has not already passed to
/// <see cref="EmitToolCallAsync"/>, and must never invoke <see cref="EmitToolCallAsync"/> twice for
/// the same <c>toolCallId</c> within one turn. A conforming implementation may assume both hold and
/// is not required to defend against a violation — <see cref="Services.ToolCallOrderingSink"/> is the
/// enforcement point that guarantees them for its wrapped sink, constructed fresh per turn by
/// <c>ExecuteAgentTurnCommandHandler</c>. A future second implementation of this interface that is
/// reached some other way (not through that decorator) must independently uphold the same contract,
/// not just replicate the method shapes.
/// </para>
/// </remarks>
public interface IAgentTurnStreamSink
{
    /// <summary>
    /// Emits a single assistant text delta to the attached consumer. Empty deltas are
    /// ignored. Honours <paramref name="cancellationToken"/> so a disconnected consumer
    /// stops the stream promptly.
    /// </summary>
    /// <param name="delta">The newly generated assistant text fragment.</param>
    /// <param name="cancellationToken">Cancels emission when the consumer goes away.</param>
    Task EmitAsync(string delta, CancellationToken cancellationToken);

    /// <summary>
    /// Emits one complete tool call the model has decided to make — its id, name, and arguments — as a
    /// single unit. Default no-op, so a sink that only cares about assistant text (or a transport that
    /// predates tool-call streaming) needs no changes. Unlike <see cref="EmitAsync"/>, <paramref
    /// name="args"/> is never an incremental delta: the underlying chat-client abstraction only ever
    /// exposes a tool call's arguments once fully assembled (the provider connector buffers raw
    /// per-chunk argument JSON internally and never surfaces a partial parse), so every field here is
    /// already complete and stable by the time this fires. A consumer that must publish start/args/end as
    /// separate wire frames (e.g. an SSE feed following the AG-UI protocol) decomposes this single call
    /// into that sequence itself — the three are always emitted together, in that order, with nothing a
    /// caller could observe between them, so splitting them at this seam would only add API surface
    /// without adding any real capability.
    /// </summary>
    /// <param name="toolCallId">The provider-assigned id for this call.</param>
    /// <param name="toolCallName">The name of the tool being called.</param>
    /// <param name="args">
    /// The tool call's arguments. <see cref="StreamedToolCallArguments.Json"/> is always complete,
    /// parseable JSON — never truncated — and is the fixed placeholder <c>"{}"</c> when
    /// <see cref="StreamedToolCallArguments.Withheld"/> is <see langword="true"/> (the real arguments
    /// exceeded the streaming size ceiling, or serialization/redaction failed).
    /// </param>
    /// <param name="cancellationToken">Cancels emission when the consumer goes away.</param>
    Task EmitToolCallAsync(string toolCallId, string toolCallName, StreamedToolCallArguments args, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Emits a tool call's result once the tool has actually run. Default no-op.
    /// </summary>
    /// <param name="toolCallId">The call this result belongs to.</param>
    /// <param name="result">The tool's output.</param>
    /// <param name="cancellationToken">Cancels emission when the consumer goes away.</param>
    Task EmitToolCallResultAsync(string toolCallId, string result, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
