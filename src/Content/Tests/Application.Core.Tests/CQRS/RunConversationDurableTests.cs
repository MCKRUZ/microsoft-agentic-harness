using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.AI.Common.Services.AI;
using Domain.AI.Budget;
using Domain.AI.Observability.Models;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// The durable half of <see cref="RunConversationCommandHandler"/>: what happens when a run continues a
/// stored conversation instead of starting a throwaway one (issue #235).
/// </summary>
/// <remarks>
/// <para>
/// Every test here is written so that removing the behaviour it covers turns it red. The four
/// invariants the issue calls load-bearing — ownership, the conversation-lifetime token budget, turn
/// serialisation, and a bounded replay window — were already solved once in the interactive host, and a
/// second implementation that quietly dropped one would be a regression wearing a feature's clothes.
/// Tests that merely pass while a control happens to be present would not have caught that.
/// </para>
/// <para>
/// Ownership is deliberately <em>not</em> re-asserted as a comparison in the handler: the store enforces
/// it. What is asserted here is that the handler hands the caller's identity to the store on every call,
/// which is the only thing it can get wrong.
/// </para>
/// </remarks>
public sealed class RunConversationDurableTests
{
    private const string ConversationId = "conv-235";
    private const string Owner = "owner-1";

    private static readonly Guid NewSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExistingSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>When each turn was dispatched, so writes can be ordered against turns.</summary>
    private readonly List<int> _dispatchSequences = [];

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly Mock<IObservabilityStore> _observability = new();
    private readonly FakeConversationStore _store = new();
    private readonly FakeTurnLease _lease = new();

    public RunConversationDurableTests()
    {
        _budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        // A store that answered Guid.Empty would make "the run adopted the session it found" and "the
        // run opened one" indistinguishable, since both would end up writing against Empty.
        _observability
            .Setup(o => o.StartSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewSessionId);
    }

    private RunConversationCommandHandler BuildSut(int maxHistoryMessages = 50) =>
        new(
            _mediator.Object,
            new Mock<IAgentConversationCache>().Object,
            _budget.Object,
            _observability.Object,
            // The real recorder over this fixture's own stores. A mocked recorder would turn every
            // assertion below — adopt-or-open, cumulative totals, turn numbering — into an assertion
            // about an interface rather than about what reaches the observability row.
            new ConversationTelemetryRecorder(
                _observability.Object, _store, NullLogger<ConversationTelemetryRecorder>.Instance),
            _store,
            _lease,
            Options.Create(new ConversationsConfig { MaxHistoryMessages = maxHistoryMessages }),
            NullLogger<RunConversationCommandHandler>.Instance);

    private static RunConversationCommand SelfContained(params string[] messages) => new()
    {
        AgentName = "TestAgent",
        ConversationId = ConversationId,
        UserMessages = messages.Length > 0 ? messages : ["hello"],
        MaxTurns = 10
    };

    private static RunConversationCommand Durable(params string[] messages) => new()
    {
        AgentName = "TestAgent",
        ConversationId = ConversationId,
        ConversationOwnerId = Owner,
        UserMessages = messages.Length > 0 ? messages : ["hello"],
        MaxTurns = 10
    };

    /// <summary>
    /// The one place turns are stubbed. Every variant below supplies only what it varies — the
    /// response and its usage — and inherits the three things every stub here must do.
    /// </summary>
    /// <remarks>
    /// Those three are load-bearing and were previously repeated per stub, which is how they drifted:
    /// honouring the token is what lets the lost-lease test observe a stopped run rather than one that
    /// finished anyway; <c>NoteTurn</c> is what lets the lease count turns and drop itself between two
    /// of them; and recording the dispatch sequence is what lets writes be ordered against turns. Two
    /// of the three stubs recorded the sequence and one did not, so swapping stubs silently lost
    /// ordering data from a test that still passed.
    /// </remarks>
    private void SetupTurnCore(Func<ExecuteAgentTurnCommand, AgentTurnResult> respond)
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns((ExecuteAgentTurnCommand cmd, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _lease.NoteTurn();
                _dispatchSequences.Add(CallSequence.Next());

                return Task.FromResult(respond(cmd));
            });
    }

    /// <summary>Builds a successful turn whose reply is <paramref name="response"/>.</summary>
    private static AgentTurnResult Answer(ExecuteAgentTurnCommand cmd, string response) => new()
    {
        Success = true,
        Response = response,
        UpdatedHistory =
        [
            .. cmd.ConversationHistory,
            new ChatMessage(ChatRole.User, cmd.UserMessage),
            new ChatMessage(ChatRole.Assistant, response)
        ]
    };

    private void SetupTurns(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        SetupTurnCore(cmd => Answer(cmd, queue.Count > 0 ? queue.Dequeue() : "done"));
    }

    // -- Continuity --

    [Fact]
    public async Task Handle_DurableRun_ReplaysStoredHistoryIntoTheFirstTurn()
    {
        // The feature itself: without this, a caller continuing a conversation is talking to an agent
        // that has never heard of it, and the only way to be understood is to resend the transcript.
        _store.History =
        [
            Message(MessageRole.User, "what did I ask before?"),
            Message(MessageRole.Assistant, "you asked about the weather")
        ];
        SetupTurns("continuing");

        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(Durable("and today?"), CancellationToken.None);

        dispatched.Should().ContainSingle();
        dispatched[0].ConversationHistory.Select(m => m.Text).Should().ContainInOrder(
            "what did I ask before?", "you asked about the weather");
    }

    [Fact]
    public async Task Handle_DurableRun_ReadsTheStoredWindowOnceAndCarriesItThroughLaterTurns()
    {
        // Later turns must build on the turn before them, not re-read the store — re-reading mid-run
        // would replay the messages this run has itself just appended, duplicating them in the prompt.
        _store.History = [Message(MessageRole.User, "earlier")];

        // CaptureDispatchedTurns answers each turn with "answer {n}", which is what the second turn's
        // inherited history must therefore contain.
        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(Durable("first", "second"), CancellationToken.None);

        _store.HistoryRequests.Should().ContainSingle("the replay window is read once, under the lease");
        dispatched.Should().HaveCount(2);
        dispatched[1].ConversationHistory.Select(m => m.Text).Should().ContainInOrder(
            "earlier", "first", "answer 1");
    }

    [Fact]
    public async Task Handle_SelfContainedRun_NeitherReadsNorWritesAnyTranscript()
    {
        // Opting out has to be total. Every existing caller of this handler omits the owner, and a
        // handler that wrote transcripts for them anyway would silently accumulate storage for
        // conversations nobody asked to keep.
        SetupTurns("answer");

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        await BuildSut().Handle(command, CancellationToken.None);

        _store.GetOrCreateCalls.Should().Be(0);
        _store.HistoryRequests.Should().BeEmpty();
        _store.Appended.Should().BeEmpty();
        _lease.AcquireCount.Should().Be(0);
    }

    // -- Bounded replay window --

    [Fact]
    public async Task Handle_DurableRun_RequestsExactlyTheConfiguredHistoryWindow()
    {
        // A transcript is unbounded; the prompt built from it must not be. Fails if the window is
        // hardcoded rather than read from configuration.
        SetupTurns("answer");

        await BuildSut(maxHistoryMessages: 7).Handle(Durable(), CancellationToken.None);

        _store.HistoryRequests.Should().ContainSingle()
            .Which.MaxMessages.Should().Be(7);
    }

    // -- Per-turn persistence --

    [Fact]
    public async Task Handle_DurableRun_PersistsUserThenAssistantForEveryTurn()
    {
        SetupTurns("first answer", "second answer");

        await BuildSut().Handle(Durable("first question", "second question"), CancellationToken.None);

        _store.Appended.Select(a => (a.Message.Role, a.Message.Content)).Should().Equal(
            (MessageRole.User, "first question"),
            (MessageRole.Assistant, "first answer"),
            (MessageRole.User, "second question"),
            (MessageRole.Assistant, "second answer"));
    }

    [Fact]
    public async Task Handle_DurableRun_AttributesEveryWriteToTheCallingOwner()
    {
        // The store is what refuses another user's conversation, and it can only do that if the caller
        // reaches it. A handler that passed anything else here would defeat the control without
        // touching it.
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _store.Appended.Should().OnlyContain(a => a.CallerId == Owner);
        _store.HistoryRequests.Should().OnlyContain(r => r.CallerId == Owner);
        _store.GetOrCreateOwners.Should().Equal(Owner);
    }

    [Fact]
    public async Task Handle_TurnFails_LeavesNoHalfTurnBehind()
    {
        // A failed turn must leave the transcript holding only complete exchanges. Writing the question
        // up-front — which the interactive transports do, so a live user can see what they asked — would
        // be wrong here: nobody is watching, and the next run REPLAYS this transcript to a model, which
        // would then see the user ask and go unanswered and would answer as if asked twice.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "model unavailable"
            });

        var result = await BuildSut().Handle(Durable("did this survive?"), CancellationToken.None);

        result.Success.Should().BeFalse();
        _store.Appended.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_TurnSucceedsWithNoText_WritesNothingRatherThanAnAnswerThatVanishesOnRead()
    {
        // A turn can legitimately succeed with no prose — a model replying with tool calls only. Storing
        // it would take the long way round to the same half-turn a failed turn would leave: BOTH stores
        // drop empty-content messages from the dispatch window (that is how widget messages are kept out
        // of prompts), so the answer would be written, filtered out on the next read, and the question
        // would stand alone forever. Every other test here uses a non-empty response, which is exactly
        // why this needs its own.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = string.Empty,
                UpdatedHistory = []
            });

        var result = await BuildSut().Handle(Durable("any tool calls?"), CancellationToken.None);

        result.Success.Should().BeTrue("an answer with no prose is not a failed turn");
        _store.Appended.Should().BeEmpty(
            "a question whose answer would be filtered out on read must not be stored alone");
    }

    // -- Conversation-lifetime token budget --

    [Fact]
    public async Task Handle_ConversationAlreadyExhausted_DispatchesNothingAndWritesNothing()
    {
        // The budget is durable and keyed by conversation, so a run continuing an exhausted one must
        // decline before its FIRST dispatch. A gate that exempted the opening turn of each run would
        // hand every new run a fresh allowance and make a lifetime ceiling meaningless.
        _budget
            .Setup(b => b.GetStatusAsync(ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationBudgetStatus(true, 5_000, 5_000));
        SetupTurns("should never run");

        var result = await BuildSut().Handle(Durable("please answer"), CancellationToken.None);

        result.BudgetExhausted.Should().BeTrue();
        result.Turns.Should().BeEmpty();
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Appended.Should().BeEmpty(
            "a question that was never asked must not appear in the transcript");
    }

    [Fact]
    public async Task Handle_DurableRun_GatesTheBudgetUnderTheConversationIdNotTheRunId()
    {
        // Keying the budget by anything run-scoped would reset the ceiling on every run.
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _budget.Verify(
            b => b.GetStatusAsync(ConversationId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _budget.Verify(
            b => b.RecordUsageAsync(ConversationId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // -- Turn serialisation --

    [Fact]
    public async Task Handle_DurableRun_HoldsTheTurnLeaseForTheWholeRunAndReleasesIt()
    {
        SetupTurns("one", "two");

        await BuildSut().Handle(Durable("first", "second"), CancellationToken.None);

        _lease.AcquireCount.Should().Be(1, "the run holds one lease, not one per turn");
        _lease.ConversationIds.Should().Equal(ConversationId);
        _lease.Released.Should().BeTrue("a lease that is never released blocks the conversation forever");
        _lease.TurnsWhileHeld.Should().Be(2, "every turn ran inside the lease");
    }

    [Fact]
    public async Task Handle_LeaseLostMidRun_StopsTheRunInsteadOfWritingOnRegardless()
    {
        // Losing the lease means another host is now taking turns on this conversation. Continuing
        // would produce exactly the interleaved transcript the lease exists to prevent, so the run has
        // to stop — which only happens if the lost-lease signal is linked into the token the turns run
        // under. Unlink it and this runs to completion with three turns instead of stopping after one.
        //
        // The lease drops during the SECOND turn, so the first is fully written and the second leaves
        // nothing: a turn interrupted between its question and its answer must not be half-persisted.
        SetupTurns("one", "two", "three");
        _lease.LoseLeaseAfterTurns = 2;

        var act = () => BuildSut().Handle(Durable("a", "b", "c"), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Appended.Select(a => (a.Message.Role, a.Message.Content)).Should().Equal(
            (MessageRole.User, "a"),
            (MessageRole.Assistant, "one"));
    }

    [Fact]
    public async Task Handle_DurableRun_OpensTheConversationBeforeTakingItsLease()
    {
        // Order is a real constraint, not a preference: the durable lease claims an existing
        // conversation row and throws when there is none, so opening has to come first.
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _store.GetOrCreateSequence.Should().BeLessThan(_lease.AcquireSequence);
    }

    [Fact]
    public async Task Handle_DurableRun_ReadsTheReplayWindowOnlyAfterTheLeaseIsHeld()
    {
        // The turn this run queued behind may have appended to the transcript. A window read before
        // the lease omits exactly those messages — the ones the run most needs to see.
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _store.HistoryRequests.Should().ContainSingle()
            .Which.Sequence.Should().BeGreaterThan(_lease.AcquireSequence);
    }

    // -- Fail-closed identity --

    [Fact]
    public async Task Handle_BlankOwner_IsRefusedRatherThanRunAsSelfContained()
    {
        // A blank identity must never read as "no owner, carry on". This codebase has repeatedly had an
        // absent identity resolve to global access, so the empty string takes the durable path and is
        // rejected there rather than quietly opting out of persistence.
        _store.GetOrCreateThrows = new ArgumentException("callerId must be non-blank");
        SetupTurns("answer");

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = ConversationId,
            ConversationOwnerId = "",
            UserMessages = ["hello"]
        };

        var act = () => BuildSut().Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ConversationOwnedByAnotherUser_PropagatesTheStoresRefusal()
    {
        // The handler must not swallow the refusal into a failed-but-successful-looking result: a
        // permission decision that reads as a model failure is a permission decision nobody audits.
        _store.GetOrCreateThrows = new ConversationAccessDeniedException();
        SetupTurns("answer");

        var act = () => BuildSut().Handle(Durable(), CancellationToken.None);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Telemetry continuity (issue #255) --

    [Fact]
    public async Task Handle_DurableRun_AddsToTheConversationsTotalsInsteadOfReplacingThem()
    {
        // The defect itself. The session row is keyed one-per-conversation and written with SET
        // semantics, so a run reporting its own totals silently replaces everything spent before it —
        // a conversation that has cost dollars reads as costing whatever its most recent run did.
        // Every figure is distinct, and every one this turn adds is distinct too, so a transposed pair
        // of arguments anywhere in the chain shows up as a wrong number rather than an equal one.
        _store.ObservabilitySessionId = ExistingSessionId;
        _store.Telemetry = new TelemetryAccumulator(
            TurnCount: 4, ToolCallCount: 3, InputTokens: 800, OutputTokens: 400,
            CacheRead: 200, CacheWrite: 100, CostUsd: 1.50m);
        SetupTurnsWithUsage(
            inputTokens: 10, outputTokens: 5, toolCalls: 1,
            cacheRead: 7, cacheWrite: 3, costUsd: 0.25m);

        await BuildSut().Handle(Durable("one more"), CancellationToken.None);

        _observability.Verify(o => o.UpdateSessionMetricsAsync(
            ExistingSessionId,
            5,      // turns:       4 + 1
            4,      // tool calls:  3 + 1
            0,
            810,    // input:     800 + 10
            405,    // output:    400 + 5
            207,    // cacheRead: 200 + 7
            103,    // cacheWrite:100 + 3
            1.75m,  // cost:      1.50 + 0.25
            It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a run continues a conversation's spend; it does not restate it");
    }

    [Fact]
    public async Task Handle_DurableRunThatThrows_LeavesTheConversationsSessionOpen()
    {
        // The exception path is the one that was missed when each call site applied the ownership rule
        // itself: a transient throw inside a durable run ended the whole conversation's session, and
        // every later run then wrote metrics into a row already marked finished.
        _store.ObservabilitySessionId = ExistingSessionId;
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the model client fell over"));

        var act = () => BuildSut().Handle(Durable(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _observability.Verify(o => o.EndSessionAsync(
            It.IsAny<Guid>(), It.IsAny<SessionStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SelfContainedRunThatThrows_EndsTheSessionItOpened()
    {
        // Control for the test above: where the run is the whole conversation, an unhandled failure
        // must still close its session out, or a crashed run leaves a row active forever.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the model client fell over"));

        var act = () => BuildSut().Handle(SelfContained(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _observability.Verify(o => o.EndSessionAsync(
            NewSessionId, SessionStatus.Error, "conversation.unhandled_exception", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DurableRun_AdoptsTheSessionTheConversationAlreadyHas()
    {
        // Opening a session that exists does not create a second one — the row is unique per
        // conversation, so the upsert restamps the first one's start time and every duration derived
        // from it collapses to the latest run.
        _store.ObservabilitySessionId = ExistingSessionId;
        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _observability.Verify(o => o.StartSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the conversation already had a session, and it only ever gets one");
        dispatched[0].ObservabilitySessionId.Should().Be(ExistingSessionId);
    }

    [Fact]
    public async Task Handle_DurableRunOnAConversationWithNoSessionYet_OpensOneAndRecordsItBeforeTheFirstTurn()
    {
        // The control for the test above: the run does still open a session when there is none, so
        // "never called StartSessionAsync" there is about the session being found, not about this
        // handler having stopped opening sessions altogether.
        //
        // Recorded BEFORE the first turn, so a run that opens a session and then declines every turn
        // on an exhausted budget still leaves the conversation pointing at it. Otherwise the next run
        // opens another and restamps the clock.
        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _observability.Verify(o => o.StartSessionAsync(
            ConversationId, "TestAgent", null, It.IsAny<CancellationToken>()), Times.Once);
        dispatched[0].ObservabilitySessionId.Should().Be(NewSessionId);

        _store.TelemetryWrites.Should().NotBeEmpty();
        _store.TelemetryWrites[0].SessionId.Should().Be(NewSessionId);
        _store.TelemetryWrites[0].Sequence.Should().BeLessThan(
            _dispatchSequences[0], "the session is recorded before the first turn runs, not after it");
    }

    [Fact]
    public async Task Handle_DurableRun_NumbersTurnsAcrossTheConversationNotWithinTheRun()
    {
        // Per-turn observability rows are keyed by conversation and turn number. A run that restarted
        // its numbering at 1 would overwrite the opening turns of the run before it.
        _store.Telemetry = TelemetryAccumulator.Zero with { TurnCount = 4 };
        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(Durable("fifth", "sixth"), CancellationToken.None);

        dispatched.Select(d => d.TurnNumber).Should().Equal([5, 6]);
    }

    [Fact]
    public async Task Handle_SelfContainedRun_NumbersTurnsFromOne()
    {
        // Control for the test above. A run with nothing behind it must still number from 1 — the
        // change is "continue the conversation's count", not "add an offset from somewhere".
        var dispatched = CaptureDispatchedTurns();

        await BuildSut().Handle(SelfContained("first", "second"), CancellationToken.None);

        dispatched.Select(d => d.TurnNumber).Should().Equal([1, 2]);
    }

    [Fact]
    public async Task Handle_DurableRun_LeavesTheConversationsSessionOpen()
    {
        // A run finishing is not the conversation finishing. Ending the session here marks a
        // conversation complete that the next run — or a user still typing in the interactive host —
        // is about to continue.
        _store.ObservabilitySessionId = ExistingSessionId;
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _observability.Verify(o => o.EndSessionAsync(
            It.IsAny<Guid>(), It.IsAny<SessionStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SelfContainedRun_EndsTheSessionItOpened()
    {
        // Control for the test above, and the reason it is not simply "the handler no longer ends
        // sessions": where the run IS the whole conversation, leaving the session open forever would
        // be the regression.
        SetupTurns("answer");

        await BuildSut().Handle(SelfContained(), CancellationToken.None);

        _observability.Verify(o => o.EndSessionAsync(
            NewSessionId, SessionStatus.Completed, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DurableRun_WritesTheRunningTotalToTheConversationAfterEveryTurn()
    {
        // The conversation's own copy is what the next run resumes from, and the only one that
        // survives the observability database being absent. Written per turn, not once at the end, so
        // a run that dies mid-way leaves behind what it actually spent.
        // Given an existing session so the only writes here are the per-turn ones. A conversation with
        // no session yet also gets a registration write up front, which is asserted separately.
        _store.ObservabilitySessionId = ExistingSessionId;
        _store.Telemetry = TelemetryAccumulator.Zero with { InputTokens = 100 };
        SetupTurnsWithUsage(inputTokens: 10, outputTokens: 5);

        await BuildSut().Handle(Durable("one", "two"), CancellationToken.None);

        _store.TelemetryWrites.Select(w => w.Telemetry.InputTokens).Should().Equal([110, 120]);
        _store.TelemetryWrites.Should().OnlyContain(w => w.CallerId == Owner,
            "the store enforces ownership on this write like every other");
    }

    [Fact]
    public async Task Handle_DurableRun_ReadsTheConversationsTotalsUnderTheLease()
    {
        // Read before the lease, a run queued behind another host's run would carry totals taken
        // before that run existed, add its own, and write back a sum missing everything the peer
        // spent — deleting a peer's telemetry rather than reporting it late.
        SetupTurns("answer");

        await BuildSut().Handle(Durable(), CancellationToken.None);

        _store.GetSequences.Should().ContainSingle("the totals are read once per run, not per turn");
        _store.GetSequences[0].Should().BeGreaterThan(
            _lease.AcquireSequence, "the totals must be read after the lease is held");
    }

    // -- Helpers --

    private List<ExecuteAgentTurnCommand> CaptureDispatchedTurns()
    {
        var dispatched = new List<ExecuteAgentTurnCommand>();
        SetupTurnCore(cmd =>
        {
            dispatched.Add(cmd);
            return Answer(cmd, $"answer {dispatched.Count}");
        });
        return dispatched;
    }

    /// <summary>
    /// Turns that report token usage, so the accumulation assertions have something to accumulate.
    /// </summary>
    private void SetupTurnsWithUsage(
        int inputTokens, int outputTokens, int toolCalls = 0,
        int cacheRead = 0, int cacheWrite = 0, decimal costUsd = 0m) =>
        SetupTurnCore(cmd => Answer(cmd, "answer") with
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            CostUsd = costUsd,
            ToolsInvoked = [.. Enumerable.Range(0, toolCalls).Select(i => $"tool-{i}")],
        });

    private static ConversationMessage Message(MessageRole role, string content) =>
        new(Guid.NewGuid(), role, content, DateTimeOffset.UtcNow);

    /// <summary>
    /// Shared monotonic counter, so tests can assert the ORDER of calls that land on two different
    /// collaborators — which is what "open before leasing" and "read the window after leasing" are.
    /// </summary>
    private static class CallSequence
    {
        private static int _next;

        public static int Next() => Interlocked.Increment(ref _next);
    }

    /// <summary>
    /// An in-memory transcript store that records what it was asked for and by whom.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than mocked because nearly every assertion here is about the arguments the
    /// handler passed and the order it passed them in, which a recording double states more directly
    /// than a pile of verifications. It enforces no ownership of its own — that is the real store's
    /// job, and a double that enforced it would prove only that the double works.
    /// </remarks>
    private sealed class FakeConversationStore : IConversationStore
    {
        public List<(string ConversationId, string CallerId, ConversationMessage Message)> Appended { get; } = [];
        public List<(string ConversationId, string CallerId, int MaxMessages, int Sequence)> HistoryRequests { get; } = [];
        public List<string> GetOrCreateOwners { get; } = [];
        public IReadOnlyList<ConversationMessage> History { get; set; } = [];
        public int GetOrCreateCalls { get; private set; }
        public int GetOrCreateSequence { get; private set; }

        /// <summary>What the conversation has already spent, as a prior run left it.</summary>
        public TelemetryAccumulator? Telemetry { get; set; }

        /// <summary>The session a prior run opened for this conversation, if any.</summary>
        public Guid? ObservabilitySessionId { get; set; }

        public List<(string CallerId, Guid SessionId, TelemetryAccumulator Telemetry, int Sequence)>
            TelemetryWrites { get; } = [];

        public List<int> GetSequences { get; } = [];

        /// <summary>When set, the next open fails with this — the store's refusals, reproduced.</summary>
        public Exception? GetOrCreateThrows { get; set; }

        public Task<ConversationRecord> GetOrCreateAsync(
            string agentName, string userId, string conversationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (GetOrCreateThrows is not null)
                return Task.FromException<ConversationRecord>(GetOrCreateThrows);

            GetOrCreateCalls++;
            GetOrCreateSequence = CallSequence.Next();
            GetOrCreateOwners.Add(userId);

            return Task.FromResult(new ConversationRecord(
                Id: conversationId,
                AgentName: agentName,
                UserId: userId,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                Messages: History));
        }

        public Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
            string conversationId, string callerId, int maxMessages, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            HistoryRequests.Add((conversationId, callerId, maxMessages, CallSequence.Next()));
            return Task.FromResult<IReadOnlyList<ConversationMessage>?>(History);
        }

        /// <remarks>
        /// <strong>Honours the cancellation token, and that is load-bearing.</strong> An earlier version
        /// of this double ignored it, which made the lost-lease test pass for the wrong reason: the
        /// handler was writing to the transcript on a token the lost lease had already cancelled, a real
        /// store would have thrown, and the test could not see it. A double that is more permissive than
        /// the thing it stands in for does not simplify a test, it silences one.
        /// </remarks>
        public Task AppendMessageAsync(
            string conversationId, string callerId, ConversationMessage message, CancellationToken ct = default) =>
            AppendMessagesAsync(conversationId, callerId, [message], ct);

        /// <remarks>
        /// All-or-nothing, like the real stores: the batch is recorded only once the token has been
        /// checked. A double that recorded messages one at a time and then threw would leave a
        /// half-written turn the production stores cannot produce, and the test asserting that a lost
        /// lease writes nothing would be asserting against a fiction.
        /// </remarks>
        public Task AppendMessagesAsync(
            string conversationId, string callerId, IReadOnlyList<ConversationMessage> messages,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var message in messages)
                Appended.Add((conversationId, callerId, message));

            return Task.CompletedTask;
        }

        public Task<ConversationRecord?> GetAsync(string conversationId, string callerId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetSequences.Add(CallSequence.Next());

            return Task.FromResult<ConversationRecord?>(new ConversationRecord(
                Id: conversationId,
                AgentName: "TestAgent",
                UserId: callerId,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                Messages: History,
                ObservabilitySessionId: ObservabilitySessionId,
                Telemetry: Telemetry));
        }
        public Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord> CreateAsync(string agentName, string userId, string? conversationId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string conversationId, string callerId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord?> TruncateFromMessageAsync(string conversationId, string callerId, Guid messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord?> UpdateSettingsAsync(string conversationId, string callerId, ConversationSettings settings, CancellationToken ct = default) =>
            throw new NotSupportedException();
        /// <remarks>
        /// Records rather than applies: keeping <see cref="Telemetry"/> fixed is what lets a test set up
        /// "the conversation has already spent this much" once and read every write the run made against
        /// it, instead of chasing a value the run is mutating underneath the assertion.
        /// </remarks>
        public Task<ConversationRecord?> UpdateTelemetryAsync(
            string conversationId, string callerId, Guid observabilitySessionId,
            TelemetryAccumulator telemetry, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TelemetryWrites.Add((callerId, observabilitySessionId, telemetry, CallSequence.Next()));
            return Task.FromResult<ConversationRecord?>(null);
        }
    }

    /// <summary>A turn lease that records how it was used and can be made to lose itself mid-run.</summary>
    private sealed class FakeTurnLease : IConversationTurnLease
    {
        private readonly List<Handle> _handles = [];

        public int AcquireCount { get; private set; }
        public int AcquireSequence { get; private set; }
        public List<string> ConversationIds { get; } = [];

        /// <summary>Turns to allow before cancelling the lease-lost token. Zero disables it.</summary>
        public int LoseLeaseAfterTurns { get; set; }

        public bool Released => _handles.Count > 0 && _handles.TrueForAll(h => h.Disposed);

        /// <summary>
        /// How many turns were dispatched while a lease was actually held — counted only when a handle
        /// exists and has not been disposed.
        /// </summary>
        /// <remarks>
        /// The "actually held" part is the whole value of the counter. Incrementing unconditionally
        /// counted turns whether or not the lease was still in hand, so a handler that released the
        /// lease before running a single turn would have left the assertion green.
        /// </remarks>
        public int TurnsWhileHeld { get; private set; }

        private Handle? Current => _handles.Count > 0 ? _handles[^1] : null;

        public Task<IConversationTurnLeaseHandle> AcquireAsync(
            string conversationId, CancellationToken ct = default)
        {
            AcquireCount++;
            AcquireSequence = CallSequence.Next();
            ConversationIds.Add(conversationId);

            var handle = new Handle();
            _handles.Add(handle);
            return Task.FromResult<IConversationTurnLeaseHandle>(handle);
        }

        /// <summary>
        /// Called by the test's turn double so the lease can count turns and, when configured, drop
        /// itself between two of them.
        /// </summary>
        public void NoteTurn()
        {
            _turnsDispatched++;

            if (Current is { Disposed: false })
                TurnsWhileHeld++;

            if (LoseLeaseAfterTurns > 0 && _turnsDispatched >= LoseLeaseAfterTurns)
                Current?.Lose();
        }

        private int _turnsDispatched;

        private sealed class Handle : IConversationTurnLeaseHandle
        {
            private readonly CancellationTokenSource _lost = new();

            public CancellationToken LeaseLost => _lost.Token;
            public bool Disposed { get; private set; }

            public void Lose() => _lost.Cancel();

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                _lost.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
