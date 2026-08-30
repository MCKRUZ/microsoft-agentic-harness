using Application.AI.Common.Interfaces;
using Application.AI.Common.Services;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Presentation.AgentHub.Tests.Streaming;

/// <summary>
/// #328's sink-level streaming invariants (I4, I6, I7) — checked directly against a
/// <see cref="StreamFrameRecorder"/>, the <c>IAgentTurnStreamSink</c> seam. The hub-level
/// invariants (I1/I2/I3/I5) are covered separately in
/// <c>AgentTelemetryHubStreamingInvariantTests</c>, since their subject has no sink-level
/// representation (see <see cref="StreamInvariants"/>'s remarks).
/// </summary>
public sealed class StreamFrameRecorderInvariantTests
{
    // ── I4 — prefix, not equality ──────────────────────────────────────────

    [Fact]
    public async Task AssertPrefix_DeltasConcatenateToExactlyTheFinalText_Passes()
    {
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitAsync("Hello, ", CancellationToken.None);
        await recorder.EmitAsync("world!", CancellationToken.None);

        var act = () => StreamInvariants.AssertPrefix(recorder, "Hello, world!");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AssertPrefix_FinalTextRepeatsTheWholeResponseOnTopOfDeltas_StillPasses()
    {
        // Mirrors the real shape: AgentTelemetryHub.EmitTurnEventsAsync re-sends the full response
        // as one more frame, and ConversationOrchestrator builds the final assistant message
        // independently of what streamed — so "final text == concatenated deltas" is NOT the
        // invariant; "concatenated deltas is a prefix of final text" is.
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitAsync("Partial", CancellationToken.None);

        var act = () => StreamInvariants.AssertPrefix(recorder, "Partial answer, fully assembled.");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AssertPrefix_DeltasDivergeFromFinalTextMidStream_ThrowsEvenThoughFinalTextIsCorrect()
    {
        // THE CONTROL for I4 (per the plan's mutation-test table): a run whose final text is
        // exactly right but whose deltas diverge mid-stream MUST fail here. If this test passes,
        // the invariant is checking only the end state and #328 is not actually closed.
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitAsync("Helxo, ", CancellationToken.None); // typo mid-stream
        await recorder.EmitAsync("world!", CancellationToken.None);

        var act = () => StreamInvariants.AssertPrefix(recorder, "Hello, world!");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*diverged from the authoritative final text*");
    }

    [Fact]
    public async Task AssertPrefix_EmptyDeltasAreIgnored_NeverAppearAsFrames()
    {
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitAsync(string.Empty, CancellationToken.None);
        await recorder.EmitAsync("real", CancellationToken.None);

        recorder.Frames.Should().ContainSingle();
        StreamInvariants.AssertPrefix(recorder, "real deal");
    }

    // ── I7 — non-decreasing elapsed ────────────────────────────────────────

    [Fact]
    public async Task AssertNonDecreasingElapsed_RealClockFrames_Passes()
    {
        var time = new FakeTimeProvider();
        var recorder = new StreamFrameRecorder(time);
        await recorder.EmitAsync("a", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await recorder.EmitAsync("b", CancellationToken.None);

        var act = () => StreamInvariants.AssertNonDecreasingElapsed(recorder.Frames);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertNonDecreasingElapsed_OutOfOrderFrames_Throws()
    {
        var frames = new List<StreamFrame>
        {
            new(StreamFrameKind.TokenDelta, "a", TimeSpan.FromMilliseconds(50)),
            new(StreamFrameKind.TokenDelta, "b", TimeSpan.FromMilliseconds(10)),
        };

        var act = () => StreamInvariants.AssertNonDecreasingElapsed(frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Frame 1*");
    }

    // ── I6 — tool-call correlation ─────────────────────────────────────────

    [Fact]
    public async Task AssertToolCallOrdering_StartThenResult_Passes()
    {
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await recorder.EmitToolCallResultAsync("call-1", new StreamedToolCallResult("ok", false), CancellationToken.None);

        var act = () => StreamInvariants.AssertToolCallOrdering(recorder.Frames);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AssertToolCallOrdering_ResultWithNoPriorStart_ThrowsWhenRecorderIsExercisedDirectly()
    {
        // Exercises StreamFrameRecorder DIRECTLY, bypassing ToolCallOrderingSink — see this file's
        // class remarks and StreamFrameRecorder's own remarks for why that placement matters: the
        // decorator would silently drop this exact call before the recorder ever saw it, and the
        // invariant could never fire.
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());

        await recorder.EmitToolCallResultAsync("orphan-call", new StreamedToolCallResult("ok", false), CancellationToken.None);

        var act = () => StreamInvariants.AssertToolCallOrdering(recorder.Frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*orphan-call*no prior ToolCallStart*");
    }

    [Fact]
    public async Task AssertToolCallOrdering_DuplicateStart_ThrowsWhenRecorderIsExercisedDirectly()
    {
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        await recorder.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);

        await recorder.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);

        var act = () => StreamInvariants.AssertToolCallOrdering(recorder.Frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate start*call-1*");
    }

    [Fact]
    public async Task AssertToolCallOrdering_TheSameViolation_IsSilentlyPreventedWhenWrappedByTheRealDecorator()
    {
        // THE CONTROL proving the recorder sits at the right layer in production: the identical
        // violation from the previous test never reaches this recorder, and therefore never fires
        // the invariant, when wrapped by the real ToolCallOrderingSink — because that decorator's
        // job is to prevent exactly this. Both this test and the two above must pass together, or
        // the recorder is either not catching real violations, or not proving the decorator works.
        var recorder = new StreamFrameRecorder(new FakeTimeProvider());
        IAgentTurnStreamSink protectedSink = new ToolCallOrderingSink(recorder);

        await protectedSink.EmitToolCallResultAsync("orphan-call", new StreamedToolCallResult("ok", false), CancellationToken.None);
        await protectedSink.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await protectedSink.EmitToolCallAsync("call-1", "search", new StreamedToolCallArguments("{}", false), CancellationToken.None);
        await protectedSink.EmitToolCallResultAsync("call-1", new StreamedToolCallResult("ok", false), CancellationToken.None);

        recorder.Frames.Should().HaveCount(2, "the orphaned result and the duplicate start were both dropped by the decorator");
        var act = () => StreamInvariants.AssertToolCallOrdering(recorder.Frames);
        act.Should().NotThrow();
    }
}
