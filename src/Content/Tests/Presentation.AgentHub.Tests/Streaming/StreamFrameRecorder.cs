using Application.AI.Common.Interfaces;

namespace Presentation.AgentHub.Tests.Streaming;

/// <summary>The kind of one captured <see cref="StreamFrame"/>.</summary>
public enum StreamFrameKind
{
    /// <summary>An assistant text delta from <see cref="IAgentTurnStreamSink.EmitAsync"/>.</summary>
    TokenDelta,

    /// <summary>A tool call start from <see cref="IAgentTurnStreamSink.EmitToolCallAsync"/>.</summary>
    ToolCallStart,

    /// <summary>A tool call result from <see cref="IAgentTurnStreamSink.EmitToolCallResultAsync"/>.</summary>
    ToolCallResult,
}

/// <summary>
/// One frame captured by <see cref="StreamFrameRecorder"/>: its kind, the frame's own identifying
/// text (the delta text for <see cref="StreamFrameKind.TokenDelta"/>, the tool-call id for the two
/// tool-call kinds), and elapsed time since the recorder was constructed.
/// </summary>
public sealed record StreamFrame(StreamFrameKind Kind, string Payload, TimeSpan Elapsed);

/// <summary>
/// Records every call made through it as an ordered, timestamped <see cref="StreamFrame"/> —
/// the direct <see cref="IAgentTurnStreamSink"/>-seam half of #328's per-frame streaming
/// invariants. Not a fake of the transport: it IS a real sink implementation, usable standalone to
/// prove <see cref="StreamInvariants.AssertPrefix"/>/<see cref="StreamInvariants.AssertNonDecreasingElapsed"/>,
/// or wrapped by the real <c>ToolCallOrderingSink</c> to prove that decorator's protection holds
/// under this recorder rather than under a hand-rolled substitute.
/// </summary>
/// <remarks>
/// For a mutation test proving <see cref="StreamInvariants.AssertToolCallOrdering"/> is a live
/// check (not dead code), the recorder must be exercised DIRECTLY — never behind
/// <c>ToolCallOrderingSink</c> — since that decorator silently drops an out-of-order call before it
/// ever reaches an inner sink. Wrapped, the invariant can never observe a violation and the test
/// would prove nothing about this type's own check.
/// </remarks>
public sealed class StreamFrameRecorder : IAgentTurnStreamSink
{
    private readonly Lock _gate = new();
    private readonly List<StreamFrame> _frames = [];
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;

    /// <summary>Creates a recorder whose <see cref="StreamFrame.Elapsed"/> values are measured
    /// from construction time using <paramref name="timeProvider"/>.</summary>
    public StreamFrameRecorder(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>All frames recorded so far, in call order.</summary>
    public IReadOnlyList<StreamFrame> Frames
    {
        get { lock (_gate) return [.. _frames]; }
    }

    /// <summary>The concatenation of every <see cref="StreamFrameKind.TokenDelta"/> payload, in order.</summary>
    public string ConcatenatedDeltas
    {
        get
        {
            var builder = new System.Text.StringBuilder();
            lock (_gate)
            {
                foreach (var frame in _frames)
                {
                    if (frame.Kind == StreamFrameKind.TokenDelta)
                        builder.Append(frame.Payload);
                }
            }
            return builder.ToString();
        }
    }

    /// <inheritdoc />
    public Task EmitAsync(string delta, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(delta)) return Task.CompletedTask;
        Record(StreamFrameKind.TokenDelta, delta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EmitToolCallAsync(string toolCallId, string toolCallName, StreamedToolCallArguments args, CancellationToken cancellationToken)
    {
        Record(StreamFrameKind.ToolCallStart, toolCallId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EmitToolCallResultAsync(string toolCallId, StreamedToolCallResult result, CancellationToken cancellationToken)
    {
        Record(StreamFrameKind.ToolCallResult, toolCallId);
        return Task.CompletedTask;
    }

    private void Record(StreamFrameKind kind, string payload)
    {
        // Elapsed must be sampled INSIDE the lock, atomically with the Add — sampling it outside
        // lets two racing calls capture their timestamps in one order but get inserted in the
        // other, producing a frame list whose position order doesn't match its own Elapsed order.
        // That would make AssertNonDecreasingElapsed (I7) fail on a race in this recorder, not on
        // a real production ordering violation.
        lock (_gate) _frames.Add(new StreamFrame(kind, payload, _timeProvider.GetElapsedTime(_startTimestamp)));
    }
}
