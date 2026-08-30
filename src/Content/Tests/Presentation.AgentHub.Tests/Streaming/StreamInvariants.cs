using System.Text.Json;
using Presentation.AgentHub.Hubs;

namespace Presentation.AgentHub.Tests.Streaming;

/// <summary>
/// One captured SignalR wire frame from <see cref="AgentTelemetryHub"/>: the event name, its
/// deserialized JSON payload, and elapsed time since capture began.
/// </summary>
public sealed record HubFrame(string EventName, JsonElement Payload, TimeSpan Elapsed);

/// <summary>
/// #328's per-frame streaming invariants. Split by seam: I4/I6/I7 are checked directly against a
/// <see cref="StreamFrameRecorder"/> (the <c>IAgentTurnStreamSink</c> level); I1/I2/I3/I5 are
/// checked against captured <see cref="HubFrame"/>s (the SignalR wire level) because their subject
/// — <c>TurnComplete</c>, <c>Error</c>, <c>HistoryTruncated</c>, and <c>isComplete</c> — has no
/// representation at the sink level at all: <c>IAgentTurnStreamSink</c> carries text deltas and
/// tool-call activity only, and <c>AgentTelemetryHub</c> never streams tool-call frames over this
/// transport (see <see cref="AgentTelemetryHub.SendMessage"/> — its <c>onChunk</c> callback is the
/// sink's only wired delegate; tool-call activity reaches clients via a separate OTel bridge, not
/// this interface). Every failure names the offending frame's index, kind/event, and elapsed time.
/// </summary>
public static class StreamInvariants
{
    /// <summary>
    /// I4 — the concatenation of every streamed delta must be a PREFIX of the authoritative final
    /// text, never required to equal it. Equality is false by design here:
    /// <c>ConversationOrchestrator.SendMessageAsync</c> constructs its own final assistant message
    /// independent of what was streamed, and <c>AgentTelemetryHub.EmitTurnEventsAsync</c> re-sends
    /// the entire response as one more frame before <c>TurnComplete</c>.
    /// </summary>
    public static void AssertPrefix(StreamFrameRecorder recorder, string authoritativeFinalText)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(authoritativeFinalText);
        var concatenated = recorder.ConcatenatedDeltas;
        if (!authoritativeFinalText.StartsWith(concatenated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Streamed deltas diverged from the authoritative final text: concatenated deltas " +
                $"\"{concatenated}\" is not a prefix of \"{authoritativeFinalText}\".");
        }
    }

    /// <summary>I7 — every recorded frame's elapsed time is >= the previous frame's.</summary>
    public static void AssertNonDecreasingElapsed(IReadOnlyList<StreamFrame> frames)
    {
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].Elapsed < frames[i - 1].Elapsed)
            {
                throw new InvalidOperationException(
                    $"Frame {i} ({frames[i].Kind}, elapsed {frames[i].Elapsed}) has a smaller elapsed " +
                    $"time than frame {i - 1} ({frames[i - 1].Kind}, elapsed {frames[i - 1].Elapsed}).");
            }
        }
    }

    /// <summary>
    /// I6 — no <see cref="StreamFrameKind.ToolCallResult"/> without a prior
    /// <see cref="StreamFrameKind.ToolCallStart"/> for the same id, and no id started twice.
    /// </summary>
    public static void AssertToolCallOrdering(IReadOnlyList<StreamFrame> frames)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Kind == StreamFrameKind.ToolCallStart)
            {
                if (!started.Add(frame.Payload))
                {
                    throw new InvalidOperationException(
                        $"Frame {i} (ToolCallStart, elapsed {frame.Elapsed}): duplicate start for id " +
                        $"'{frame.Payload}', already started earlier in this turn.");
                }
            }
            else if (frame.Kind == StreamFrameKind.ToolCallResult && !started.Contains(frame.Payload))
            {
                throw new InvalidOperationException(
                    $"Frame {i} (ToolCallResult, elapsed {frame.Elapsed}): result for id '{frame.Payload}' " +
                    "with no prior ToolCallStart in this turn.");
            }
        }
    }

    /// <summary>I1 — exactly one terminal frame (TurnComplete XOR Error) and it is the last frame.</summary>
    public static void AssertExactlyOneTerminalFrame(IReadOnlyList<HubFrame> frames)
    {
        var terminal = frames
            .Select((f, i) => (Frame: f, Index: i))
            .Where(t => t.Frame.EventName is AgentTelemetryHub.EventTurnComplete or AgentTelemetryHub.EventError)
            .ToList();

        if (terminal.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one terminal frame (TurnComplete XOR Error); found {terminal.Count}: " +
                string.Join(", ", terminal.Select(t => $"[{t.Index}] {t.Frame.EventName}@{t.Frame.Elapsed}")));
        }

        var (frame, index) = terminal[0];
        if (index != frames.Count - 1)
        {
            throw new InvalidOperationException(
                $"Terminal frame at index {index} ({frame.EventName}, elapsed {frame.Elapsed}) is not the " +
                $"last frame — {frames.Count - 1 - index} frame(s) followed it.");
        }
    }

    /// <summary>
    /// I2 — exactly one <c>TokenReceived</c> frame with <c>isComplete=true</c> on a successful turn
    /// (zero on an errored one). Uniqueness alone is what's checked: once at most one such frame is
    /// confirmed to exist, there is nothing earlier in the list left to also be one.
    /// </summary>
    public static void AssertExactlyOneCompletionToken(IReadOnlyList<HubFrame> frames)
    {
        var hasError = false;
        var completionIndices = new List<int>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].EventName == AgentTelemetryHub.EventError)
                hasError = true;
            else if (frames[i].EventName == AgentTelemetryHub.EventTokenReceived
                && frames[i].Payload.GetProperty("isComplete").GetBoolean())
                completionIndices.Add(i);
        }

        if (hasError)
        {
            if (completionIndices.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Errored turn emitted {completionIndices.Count} isComplete=true TokenReceived frame(s) " +
                    $"(first at index {completionIndices[0]}, elapsed {frames[completionIndices[0]].Elapsed}); expected zero.");
            }
            return;
        }

        if (completionIndices.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one isComplete=true TokenReceived frame on a successful turn; found {completionIndices.Count}: " +
                string.Join(", ", completionIndices.Select(i => $"[{i}]@{frames[i].Elapsed}")));
        }
    }

    /// <summary>Returns the index of the first frame with the given event name, or -1 if none.</summary>
    private static int FindFirstIndex(IReadOnlyList<HubFrame> frames, string eventName)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].EventName == eventName) return i;
        }
        return -1;
    }

    /// <summary>I3 — <c>HistoryTruncated</c>, when present, precedes every <c>TokenReceived</c> frame.</summary>
    public static void AssertHistoryTruncatedPrecedesTokens(IReadOnlyList<HubFrame> frames)
    {
        var truncatedIndex = FindFirstIndex(frames, AgentTelemetryHub.EventHistoryTruncated);
        if (truncatedIndex < 0) return;

        var firstTokenIndex = FindFirstIndex(frames, AgentTelemetryHub.EventTokenReceived);
        if (firstTokenIndex >= 0 && firstTokenIndex < truncatedIndex)
        {
            throw new InvalidOperationException(
                $"HistoryTruncated at index {truncatedIndex} (elapsed {frames[truncatedIndex].Elapsed}) " +
                $"arrived AFTER the first TokenReceived at index {firstTokenIndex} " +
                $"(elapsed {frames[firstTokenIndex].Elapsed}).");
        }
    }

    /// <summary>I5 — every frame in the turn carries the same <c>conversationId</c>.</summary>
    public static void AssertSameConversationId(IReadOnlyList<HubFrame> frames, string expectedConversationId)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var actual = frames[i].Payload.GetProperty("conversationId").GetString();
            if (actual != expectedConversationId)
            {
                throw new InvalidOperationException(
                    $"Frame {i} ({frames[i].EventName}, elapsed {frames[i].Elapsed}) carries conversationId " +
                    $"'{actual}', expected '{expectedConversationId}'.");
            }
        }
    }
}
