using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.AI;

/// <summary>
/// The one policy three transports used to carry a copy of (issue #280), tested directly rather than
/// only through them.
/// </summary>
/// <remarks>
/// Each transport's own tests prove it delegates here. What they cannot show — because each exercises
/// one shape of caller — is that the policy itself is right for every shape: adopting instead of
/// re-opening, refusing a blank identity, writing nothing durable for a run that has no record, and
/// never failing a turn to report an accounting problem.
/// </remarks>
public sealed class ConversationTelemetryRecorderTests
{
    private const string ConversationId = "conv-280";
    private const string Owner = "owner-1";

    private static readonly Guid ExistingSession = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NewSession = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IObservabilityStore> _observability = new();
    private readonly Mock<IConversationStore> _conversations = new();

    public ConversationTelemetryRecorderTests()
    {
        _observability
            .Setup(o => o.StartSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSession);
    }

    private ConversationTelemetryRecorder Sut() =>
        new(_observability.Object, _conversations.Object, NullLogger<ConversationTelemetryRecorder>.Instance);

    private static ConversationRecord Record(Guid? session, TelemetryAccumulator? totals = null) =>
        new(ConversationId, "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [])
        {
            ObservabilitySessionId = session,
            Telemetry = totals,
        };

    // ── Adopt or open ────────────────────────────────────────────────────

    [Fact]
    public async Task BeginAsync_ConversationAlreadyHasASession_AdoptsItWithoutOpeningAnother()
    {
        var totals = new TelemetryAccumulator(5, 2, 100, 50, 10, 5, 0.5m);
        _conversations
            .Setup(s => s.GetAsync(ConversationId, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(ExistingSession, totals));

        var state = await Sut().BeginAsync(ConversationId, Owner, "agent");

        state.SessionId.Should().Be(ExistingSession);
        state.Totals.Should().Be(totals);
        state.SessionOpened.Should().BeFalse();

        _observability.Verify(
            o => o.StartSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a conversation has one session for its whole life — opening a second restamps the first "
            + "one's start time and every duration derived from it");
    }

    [Fact]
    public async Task BeginAsync_NoSessionYet_OpensOneAndRecordsItBeforeAnyTurn()
    {
        _conversations
            .Setup(s => s.GetAsync(ConversationId, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(session: null));

        var state = await Sut().BeginAsync(ConversationId, Owner, "agent");

        state.SessionId.Should().Be(NewSession);
        state.SessionOpened.Should().BeTrue();

        // Written before the first turn, so a crash in between does not leave the conversation opening
        // a second session next time.
        _conversations.Verify(
            s => s.UpdateTelemetryAsync(
                ConversationId, Owner, NewSession, TelemetryAccumulator.Zero, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BeginAsync_AdoptingASession_UnEndsItSoTheRowStopsClaimingTheConversationFinished()
    {
        // The session row is keyed one per conversation, but the decision to end one is taken per
        // connection: the hub ends a session when the connection holding it disconnects or switches
        // away, which says nothing about whether the conversation is over. Coming back to it wrote every
        // further turn into a row marked finished, with a past end time and a climbing duration (#289).
        //
        // Adoption is the assertion that the conversation is live, so this is its counterpart. It runs
        // unconditionally because the recorder cannot tell an ended row from a live one without a read,
        // and the store's WHERE clause makes the live case a no-op.
        _conversations
            .Setup(s => s.GetAsync(ConversationId, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(ExistingSession, TelemetryAccumulator.Zero));

        await Sut().BeginAsync(ConversationId, Owner, "agent");

        _observability.Verify(
            o => o.ResumeSessionAsync(ExistingSession, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BeginAsync_AdoptingFromASuppliedRecord_UnEndsItToo()
    {
        // The AG-UI path supplies the record it already read under its lease rather than letting the
        // recorder read a third time, so it reaches adoption by a different branch. A fix applied to one
        // branch and not the other is the shape of mistake this codebase has shipped four times.
        await Sut().BeginAsync(
            ConversationId, Owner, "agent", knownRecord: Record(ExistingSession, TelemetryAccumulator.Zero));

        _observability.Verify(
            o => o.ResumeSessionAsync(ExistingSession, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BeginAsync_OpeningTheFirstSession_HasNothingToUnEnd()
    {
        _conversations
            .Setup(s => s.GetAsync(ConversationId, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(session: null));

        await Sut().BeginAsync(ConversationId, Owner, "agent");

        _observability.Verify(
            o => o.ResumeSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a session opened moments ago is already active; resuming it would be a write with nothing "
            + "to say");
    }

    [Fact]
    public async Task BeginAsync_TurnNumberContinuesTheConversation()
    {
        _conversations
            .Setup(s => s.GetAsync(ConversationId, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(ExistingSession, TelemetryAccumulator.Zero with { TurnCount = 6 }));

        var state = await Sut().BeginAsync(ConversationId, Owner, "agent");

        state.NextTurnNumber.Should().Be(7);
    }

    // ── Identity ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginAsync_BlankCaller_IsRefusedRatherThanTreatedAsAbsent()
    {
        // A null caller is a run with no transcript. A blank one is a bug upstream, and this codebase
        // has read an empty identity as "everyone" before — so the two must not collapse.
        var begin = async () => await Sut().BeginAsync(ConversationId, "   ", "agent");

        await begin.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BeginAsync_NoCaller_TouchesNoConversationRecordAtAll()
    {
        var strict = new Mock<IConversationStore>(MockBehavior.Strict);
        var sut = new ConversationTelemetryRecorder(
            _observability.Object, strict.Object, NullLogger<ConversationTelemetryRecorder>.Instance);

        var state = await sut.BeginAsync(ConversationId, callerId: null, "agent");

        // Strict: any call at all would throw. A run with no owner has no record to read or write, and
        // guessing one would be the identity-shaped mistake this guards against.
        state.SessionId.Should().Be(NewSession);
        state.CallerId.Should().BeNull();
    }

    [Fact]
    public async Task BeginAsync_KnownRecordForADifferentConversation_IsRefused()
    {
        var wrong = new ConversationRecord(
            "someone-elses-conversation", "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

        var begin = async () => await Sut().BeginAsync(ConversationId, Owner, "agent", wrong);

        await begin.Should().ThrowAsync<ArgumentException>(
            "adopting a session off the wrong record would write this conversation's turns into another "
            + "conversation's row");
    }

    [Fact]
    public async Task BeginAsync_KnownRecordOwnedBySomeoneElse_IsRefused()
    {
        var wrong = new ConversationRecord(
            ConversationId, "agent", "a-different-owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

        var begin = async () => await Sut().BeginAsync(ConversationId, Owner, "agent", wrong);

        await begin.Should().ThrowAsync<ArgumentException>();
    }

    // ── Recording ────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordTurnAsync_WritesTheConversationsCumulativeTotals_NotTheTurnsOwn()
    {
        var state = new ConversationTelemetryState(
            ConversationId, Owner, ExistingSession,
            new TelemetryAccumulator(3, 1, 1_000, 500, 200, 100, 2m));

        var updated = await Sut().RecordTurnAsync(
            state, new ConversationTurnTelemetry(10, 20, 5, 5, 0.25m, 2, "gpt-4o"));

        updated.Totals.TurnCount.Should().Be(4);
        updated.Totals.InputTokens.Should().Be(1_010);

        _observability.Verify(
            o => o.UpdateSessionMetricsAsync(
                ExistingSession, 4, 3, 0, 1_010, 520, 205, 105, 2.25m,
                It.IsAny<decimal>(), "gpt-4o", It.IsAny<CancellationToken>()),
            Times.Once,
            "the row is keyed one-per-conversation and written with SET semantics, so a run's own share "
            + "would replace the conversation's history with it");
    }

    [Fact]
    public async Task RecordTurnAsync_NoCaller_WritesTheRollupButNoConversationRecord()
    {
        var strict = new Mock<IConversationStore>(MockBehavior.Strict);
        var sut = new ConversationTelemetryRecorder(
            _observability.Object, strict.Object, NullLogger<ConversationTelemetryRecorder>.Instance);

        var state = new ConversationTelemetryState(
            ConversationId, CallerId: null, NewSession, TelemetryAccumulator.Zero);

        await sut.RecordTurnAsync(state, new ConversationTurnTelemetry(1, 1, 0, 0, 0m, 0));

        _observability.Verify(
            o => o.UpdateSessionMetricsAsync(
                NewSession, 1, 0, 0, 1, 1, 0, 0, 0m,
                It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordTurnAsync_StoreThrows_DoesNotFailTheTurn()
    {
        // The contract the interface states, and the one that matters: the turn has already happened and
        // its answer is already in the transcript, so discarding real work to report an accounting
        // problem is the wrong trade.
        _observability
            .Setup(o => o.UpdateSessionMetricsAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("observability database is down"));

        var state = new ConversationTelemetryState(
            ConversationId, Owner, ExistingSession, TelemetryAccumulator.Zero);

        var updated = await Sut().RecordTurnAsync(state, new ConversationTurnTelemetry(7, 3, 0, 0, 0m, 0));

        updated.Totals.TurnCount.Should().Be(1,
            "the returned state still has to carry the turn, or the next one resumes from the wrong "
            + "place and the failure compounds instead of being caught up by the next write");
        updated.Totals.InputTokens.Should().Be(7);
    }
}
