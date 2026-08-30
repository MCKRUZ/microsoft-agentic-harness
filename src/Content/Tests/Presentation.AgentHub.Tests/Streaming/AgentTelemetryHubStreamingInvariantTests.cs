using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Xunit;

namespace Presentation.AgentHub.Tests.Streaming;

/// <summary>
/// #328's hub-level streaming invariants (I1, I2, I3, I5) — checked against real SignalR wire
/// frames captured off a directly-constructed <see cref="AgentTelemetryHub"/> (mocked
/// <see cref="IHubCallerClients"/>/<see cref="HubCallerContext"/>, real hub code). I4/I6/I7 are
/// covered separately in <c>StreamFrameRecorderInvariantTests</c> — see <see cref="StreamInvariants"/>'s
/// remarks for why the split follows the seam, not the invariant numbering.
/// </summary>
public sealed class AgentTelemetryHubStreamingInvariantTests
{
    // Matches the JsonHubProtocol default in ASP.NET Core SignalR — same policy
    // SignalRContextSnapshotNotifierTests uses for the identical reason.
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Fixture(
        AgentTelemetryHub Hub,
        Mock<IConversationOrchestrator> Orchestrator,
        List<HubFrame> Captured,
        CancellationTokenSource ConnectionAborted);

    private static Fixture Build()
    {
        var orchestrator = new Mock<IConversationOrchestrator>();
        var clientProxy = new Mock<ISingleClientProxy>();
        var callerClients = new Mock<IHubCallerClients>();
        callerClients.SetupGet(c => c.Caller).Returns(clientProxy.Object);

        var captured = new List<HubFrame>();
        var gate = new object();
        var stopwatch = Stopwatch.StartNew();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns((string method, object?[] args, CancellationToken _) =>
            {
                var frame = new HubFrame(method, JsonSerializer.SerializeToElement(args[0], WireJson), stopwatch.Elapsed);
                lock (gate) captured.Add(frame);
                return Task.CompletedTask;
            });

        var connectionAborted = new CancellationTokenSource();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionAborted).Returns(connectionAborted.Token);
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.User).Returns(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-user")], "test")));

        var hub = new AgentTelemetryHub(orchestrator.Object, NullLogger<AgentTelemetryHub>.Instance)
        {
            Clients = callerClients.Object,
            Context = context.Object,
        };

        return new Fixture(hub, orchestrator, captured, connectionAborted);
    }

    /// <summary>Scripts the mocked orchestrator to stream <paramref name="deltas"/> via <c>onChunk</c>
    /// (mirroring what <c>ExecuteAgentTurnCommandHandler.RunStreamingTurnAsync</c> really does through
    /// this seam) before returning <paramref name="outcome"/>.</summary>
    private static void ScriptSuccessfulStream(Mock<IConversationOrchestrator> orchestrator, IReadOnlyList<string> deltas, TurnOutcome outcome)
    {
        orchestrator
            .Setup(o => o.SendMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Guid _, string _, string _,
                Func<string, CancellationToken, Task>? onChunk, CancellationToken ct) =>
            {
                if (onChunk is not null)
                {
                    foreach (var delta in deltas)
                        await onChunk(delta, ct);
                }
                return outcome;
            });
    }

    /// <summary>Scripts the mocked orchestrator to return a failed outcome directly (no streaming).</summary>
    private static void ScriptErrorOutcome(Mock<IConversationOrchestrator> orchestrator, string errorMessage)
    {
        orchestrator
            .Setup(o => o.SendMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TurnOutcome { Success = false, ErrorMessage = errorMessage });
    }

    // ── I1 — exactly one terminal frame, and it is last ─────────────────────

    [Fact]
    public async Task SuccessfulTurn_TerminalFrameIsTurnCompleteAndIsLast()
    {
        var fixture = Build();
        ScriptSuccessfulStream(fixture.Orchestrator, ["Hello, ", "world!"],
            new TurnOutcome { Success = true, Response = "Hello, world!", FinalTurnNumber = 1 });

        await fixture.Hub.SendMessage("conv-1", Guid.NewGuid(), "Hi");

        var act = () => StreamInvariants.AssertExactlyOneTerminalFrame(fixture.Captured);
        act.Should().NotThrow();
        fixture.Captured[^1].EventName.Should().Be(AgentTelemetryHub.EventTurnComplete);
    }

    [Fact]
    public async Task FailedTurn_TerminalFrameIsErrorAndIsLast()
    {
        var fixture = Build();
        ScriptErrorOutcome(fixture.Orchestrator, "model unavailable");

        await fixture.Hub.SendMessage("conv-1", Guid.NewGuid(), "Hi");

        var act = () => StreamInvariants.AssertExactlyOneTerminalFrame(fixture.Captured);
        act.Should().NotThrow();
        fixture.Captured[^1].EventName.Should().Be(AgentTelemetryHub.EventError);
    }

    [Fact]
    public void AssertExactlyOneTerminalFrame_FrameAfterTerminal_Throws()
    {
        // The check itself, proven against a synthetic violation — production code never emits
        // anything after TurnComplete, so this cannot be driven through the real hub.
        var frames = new List<HubFrame>
        {
            new(AgentTelemetryHub.EventTurnComplete, JsonDocument.Parse("{}").RootElement, TimeSpan.FromMilliseconds(10)),
            new(AgentTelemetryHub.EventTokenReceived, JsonDocument.Parse("{}").RootElement, TimeSpan.FromMilliseconds(20)),
        };

        var act = () => StreamInvariants.AssertExactlyOneTerminalFrame(frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not the last frame*");
    }

    // ── I2 — exactly one isComplete=true TokenReceived on success, zero on error ────

    [Fact]
    public async Task SuccessfulTurn_ExactlyOneCompletionToken()
    {
        var fixture = Build();
        ScriptSuccessfulStream(fixture.Orchestrator, ["a", "b", "c"],
            new TurnOutcome { Success = true, Response = "abc", FinalTurnNumber = 1 });

        await fixture.Hub.SendMessage("conv-1", Guid.NewGuid(), "Hi");

        var act = () => StreamInvariants.AssertExactlyOneCompletionToken(fixture.Captured);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task FailedTurn_ZeroCompletionTokens()
    {
        var fixture = Build();
        ScriptErrorOutcome(fixture.Orchestrator, "boom");

        await fixture.Hub.SendMessage("conv-1", Guid.NewGuid(), "Hi");

        var act = () => StreamInvariants.AssertExactlyOneCompletionToken(fixture.Captured);
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertExactlyOneCompletionToken_TwoCompletionFrames_Throws()
    {
        var frames = new List<HubFrame>
        {
            new(AgentTelemetryHub.EventTokenReceived, JsonDocument.Parse("""{"isComplete":true}""").RootElement, TimeSpan.FromMilliseconds(10)),
            new(AgentTelemetryHub.EventTokenReceived, JsonDocument.Parse("""{"isComplete":true}""").RootElement, TimeSpan.FromMilliseconds(20)),
            new(AgentTelemetryHub.EventTurnComplete, JsonDocument.Parse("{}").RootElement, TimeSpan.FromMilliseconds(30)),
        };

        var act = () => StreamInvariants.AssertExactlyOneCompletionToken(frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*found 2*");
    }

    // ── I3 — HistoryTruncated precedes every TokenReceived ───────────────────

    [Fact]
    public async Task RetryFromMessage_HistoryTruncatedPrecedesEveryTokenReceived()
    {
        // Must run the retry path: HistoryKeepCount is only ever set on retry/edit, so the plain
        // SendMessage path never emits HistoryTruncated at all and a version of this test using it
        // would prove nothing (the plan's own stated trap for I3).
        //
        // THE CONTROL for I3: first run of this test (before the fix landed) caught a real bug —
        // AgentTelemetryHub.EmitTurnEventsAsync emitted HistoryTruncated only AFTER the whole turn
        // completed, so a retry/edit's own streamed deltas (emitted via onChunk DURING
        // IConversationOrchestrator.RetryFromMessageAsync/EditAndResubmitAsync, before that method
        // returns) always arrived first — a client could append the new response onto its still-
        // untruncated message list before being told to drop the stale tail. Fixed by threading an
        // onHistoryTruncated callback through the orchestrator interface, invoked immediately after
        // truncation and before dispatch (ConversationOrchestrator.cs), and moving the hub's own
        // emission out of the post-turn EmitTurnEventsAsync into that callback (#328).
        var fixture = Build();
        fixture.Orchestrator
            .Setup(o => o.RetryFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<Func<int, CancellationToken, Task>?>()))
            .Returns(async (string _, string _, Guid _, string _,
                Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
                Func<int, CancellationToken, Task>? onHistoryTruncated) =>
            {
                if (onHistoryTruncated is not null)
                    await onHistoryTruncated(4, ct);
                if (onChunk is not null)
                    await onChunk("Recovered", ct);
                return new TurnOutcome { Success = true, Response = "Recovered", FinalTurnNumber = 2, HistoryKeepCount = 4 };
            });

        await fixture.Hub.RetryFromMessage("conv-1", Guid.NewGuid());

        fixture.Captured.Should().Contain(f => f.EventName == AgentTelemetryHub.EventHistoryTruncated);
        fixture.Captured[0].EventName.Should().Be(AgentTelemetryHub.EventHistoryTruncated,
            "the truncation signal must reach the client before this turn's own streamed deltas");
        var act = () => StreamInvariants.AssertHistoryTruncatedPrecedesTokens(fixture.Captured);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task RetryFromMessage_FailsAfterTruncationWasSignaled_EmitsErrorFrameNotBareRpcRejection()
    {
        // Code-review finding: moving HistoryTruncated before dispatch (the #328 fix above) opened
        // a narrower gap — if something fails AFTER the client was told to drop its stale tail but
        // BEFORE a real TurnComplete/Error frame would normally follow, a bare HubException only
        // reaches the RPC caller's own .catch(), never the client's dedicated Error event handler,
        // leaving the transcript truncated with no explanation. AgentTelemetryHub now tracks whether
        // the truncation notice was signalled and, if so, emits a real Error frame before rethrowing.
        var fixture = Build();
        fixture.Orchestrator
            .Setup(o => o.RetryFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<Func<int, CancellationToken, Task>?>()))
            .Returns(async (string _, string _, Guid _, string _,
                Func<string, CancellationToken, Task>? _, CancellationToken ct,
                Func<int, CancellationToken, Task>? onHistoryTruncated) =>
            {
                if (onHistoryTruncated is not null)
                    await onHistoryTruncated(4, ct);
                throw new IOException("store write failed after truncation");
            });

        var act = () => fixture.Hub.RetryFromMessage("conv-1", Guid.NewGuid());

        await act.Should().ThrowAsync<IOException>("the failure still propagates to the RPC caller too");
        fixture.Captured.Should().ContainSingle(f => f.EventName == AgentTelemetryHub.EventHistoryTruncated);
        fixture.Captured.Should().Contain(f => f.EventName == AgentTelemetryHub.EventError,
            "the client already acted on the truncation notice and must be told the turn failed");
    }

    [Fact]
    public async Task RetryFromMessage_TypedExceptionAfterTruncationWasSignaled_StillEmitsErrorFrame()
    {
        // THE CONTROL for the exact gap `run-gates`' correctness reviewer caught: the first version
        // of this fix put `catch (Exception) when (historyTruncatedSignaled)` BELOW the pre-existing
        // `catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)`
        // — C# matches catch clauses top-down, so for InvalidOperationException/UnauthorizedAccessException
        // (a stolen turn lease, a mid-flight ConversationAccessDeniedException — the types this
        // method actually throws on this path) the typed clause won FIRST and the new guard never
        // ran, silently defeating it for the exact failures it exists for. This test uses the same
        // exception type ConversationOrchestrator's own lease-conflict path throws
        // (InvalidOperationException(ConversationLeaseNotice.Message)) rather than an arbitrary one,
        // specifically so it cannot pass by accident the way the IOException-based test above could.
        var fixture = Build();
        fixture.Orchestrator
            .Setup(o => o.RetryFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<Func<int, CancellationToken, Task>?>()))
            .Returns(async (string _, string _, Guid _, string _,
                Func<string, CancellationToken, Task>? _, CancellationToken ct,
                Func<int, CancellationToken, Task>? onHistoryTruncated) =>
            {
                if (onHistoryTruncated is not null)
                    await onHistoryTruncated(4, ct);
                throw new InvalidOperationException("Another host now owns this conversation's turn.");
            });

        var act = () => fixture.Hub.RetryFromMessage("conv-1", Guid.NewGuid());

        // Still wrapped as a HubException — existing typed-exception behaviour for RPC callers is
        // unchanged — but the Error frame must ALSO have gone out first, unlike before the fix.
        await act.Should().ThrowAsync<HubException>();
        fixture.Captured.Should().ContainSingle(f => f.EventName == AgentTelemetryHub.EventHistoryTruncated);
        fixture.Captured.Should().Contain(f => f.EventName == AgentTelemetryHub.EventError,
            "an InvalidOperationException after truncation must not silently skip the Error frame");
    }

    [Fact]
    public void AssertHistoryTruncatedPrecedesTokens_TruncatedAfterFirstDelta_Throws()
    {
        // The check itself, proven against a synthetic violation — production code always emits
        // HistoryTruncated unconditionally first when present, so this cannot be driven through
        // the real hub either.
        var frames = new List<HubFrame>
        {
            new(AgentTelemetryHub.EventTokenReceived, JsonDocument.Parse("""{"isComplete":false}""").RootElement, TimeSpan.FromMilliseconds(10)),
            new(AgentTelemetryHub.EventHistoryTruncated, JsonDocument.Parse("{}").RootElement, TimeSpan.FromMilliseconds(20)),
        };

        var act = () => StreamInvariants.AssertHistoryTruncatedPrecedesTokens(frames);

        act.Should().Throw<InvalidOperationException>().WithMessage("*arrived AFTER*");
    }

    // ── I5 — every frame carries the same conversationId ────────────────────

    [Fact]
    public async Task SuccessfulTurn_EveryFrameCarriesTheSameConversationId()
    {
        var fixture = Build();
        ScriptSuccessfulStream(fixture.Orchestrator, ["a", "b"],
            new TurnOutcome { Success = true, Response = "ab", FinalTurnNumber = 1 });

        await fixture.Hub.SendMessage("conv-42", Guid.NewGuid(), "Hi");

        var act = () => StreamInvariants.AssertSameConversationId(fixture.Captured, "conv-42");
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertSameConversationId_MismatchedFrame_Throws()
    {
        var frames = new List<HubFrame>
        {
            new(AgentTelemetryHub.EventTokenReceived, JsonDocument.Parse("""{"conversationId":"conv-1"}""").RootElement, TimeSpan.FromMilliseconds(10)),
            new(AgentTelemetryHub.EventTurnComplete, JsonDocument.Parse("""{"conversationId":"conv-WRONG"}""").RootElement, TimeSpan.FromMilliseconds(20)),
        };

        var act = () => StreamInvariants.AssertSameConversationId(frames, "conv-1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*conv-WRONG*");
    }

    // ── Mid-stream interruption (ConversationOrchestrator.cs:409-420 behaviour) ─────

    [Fact]
    public async Task ClientDisconnectMidStream_StopsWithNoTerminalFrame()
    {
        // Pins ConversationOrchestrator.SendMessageAsync's documented behaviour: a cancelled
        // connection token mid-turn is a routine disconnect, rethrown WITHOUT ever reaching
        // EmitTurnEventsAsync — no TurnComplete, no Error, just the deltas that streamed before
        // the disconnect.
        var fixture = Build();
        fixture.Orchestrator
            .Setup(o => o.SendMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Func<string, CancellationToken, Task>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Guid _, string _, string _,
                Func<string, CancellationToken, Task>? onChunk, CancellationToken ct) =>
            {
                if (onChunk is not null)
                {
                    await onChunk("Partial before disconnect", ct);
                }

                fixture.ConnectionAborted.Cancel();
                throw new OperationCanceledException(ct);
            });

        var act = () => fixture.Hub.SendMessage("conv-1", Guid.NewGuid(), "Hi");

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Captured.Should().ContainSingle(f => f.EventName == AgentTelemetryHub.EventTokenReceived);
        fixture.Captured.Should().NotContain(f => f.EventName == AgentTelemetryHub.EventTurnComplete);
        fixture.Captured.Should().NotContain(f => f.EventName == AgentTelemetryHub.EventError);
    }
}
