using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Budget;
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

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly FakeConversationStore _store = new();
    private readonly FakeTurnLease _lease = new();

    public RunConversationDurableTests()
    {
        _budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);
    }

    private RunConversationCommandHandler BuildSut(int maxHistoryMessages = 50) =>
        new(
            _mediator.Object,
            new Mock<IAgentConversationCache>().Object,
            _budget.Object,
            new Mock<IObservabilityStore>().Object,
            _store,
            _lease,
            Options.Create(new ConversationsConfig { MaxHistoryMessages = maxHistoryMessages }),
            NullLogger<RunConversationCommandHandler>.Instance);

    private static RunConversationCommand Durable(params string[] messages) => new()
    {
        AgentName = "TestAgent",
        ConversationId = ConversationId,
        ConversationOwnerId = Owner,
        UserMessages = messages.Length > 0 ? messages : ["hello"],
        MaxTurns = 10
    };

    private void SetupTurns(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns((ExecuteAgentTurnCommand cmd, CancellationToken ct) =>
            {
                // Honouring the token is what lets the lost-lease test observe a stopped run rather
                // than a run that finished anyway.
                ct.ThrowIfCancellationRequested();
                _lease.NoteTurn();

                var response = queue.Count > 0 ? queue.Dequeue() : "done";
                return Task.FromResult(new AgentTurnResult
                {
                    Success = true,
                    Response = response,
                    UpdatedHistory =
                    [
                        .. cmd.ConversationHistory,
                        new ChatMessage(ChatRole.User, cmd.UserMessage),
                        new ChatMessage(ChatRole.Assistant, response)
                    ]
                });
            });
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
    public async Task Handle_TurnFails_KeepsTheQuestionAndRecordsNoAnswer()
    {
        // Persisting per turn is the reason a run that dies partway keeps what it completed. The
        // question survives because it was written before the dispatch that failed.
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
        _store.Appended.Should().ContainSingle();
        _store.Appended[0].Message.Role.Should().Be(MessageRole.User);
        _store.Appended[0].Message.Content.Should().Be("did this survive?");
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
        // under. Unlink it and this test hangs on to completion with three turns instead of one.
        SetupTurns("one", "two", "three");
        _lease.LoseLeaseAfterTurns = 1;

        var act = () => BuildSut().Handle(Durable("a", "b", "c"), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Appended.Count(a => a.Message.Role == MessageRole.Assistant).Should().Be(1,
            "only the turn that completed before the lease was lost may reach the transcript");
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

    // -- Helpers --

    private List<ExecuteAgentTurnCommand> CaptureDispatchedTurns()
    {
        var dispatched = new List<ExecuteAgentTurnCommand>();
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns((ExecuteAgentTurnCommand cmd, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                _lease.NoteTurn();
                dispatched.Add(cmd);

                var response = $"answer {dispatched.Count}";
                return Task.FromResult(new AgentTurnResult
                {
                    Success = true,
                    Response = response,
                    UpdatedHistory =
                    [
                        .. cmd.ConversationHistory,
                        new ChatMessage(ChatRole.User, cmd.UserMessage),
                        new ChatMessage(ChatRole.Assistant, response)
                    ]
                });
            });
        return dispatched;
    }

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

        /// <summary>When set, the next open fails with this — the store's refusals, reproduced.</summary>
        public Exception? GetOrCreateThrows { get; set; }

        public Task<ConversationRecord> GetOrCreateAsync(
            string agentName, string userId, string conversationId, CancellationToken ct = default)
        {
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
            HistoryRequests.Add((conversationId, callerId, maxMessages, CallSequence.Next()));
            return Task.FromResult<IReadOnlyList<ConversationMessage>?>(History);
        }

        public Task AppendMessageAsync(
            string conversationId, string callerId, ConversationMessage message, CancellationToken ct = default)
        {
            Appended.Add((conversationId, callerId, message));
            return Task.CompletedTask;
        }

        public Task<ConversationRecord?> GetAsync(string conversationId, string callerId, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
        public Task<ConversationRecord?> UpdateTelemetryAsync(string conversationId, string callerId, Guid observabilitySessionId, TelemetryAccumulator telemetry, CancellationToken ct = default) =>
            throw new NotSupportedException();
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

        /// <summary>How many turns were dispatched while a lease was held.</summary>
        public int TurnsWhileHeld { get; private set; }

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
            TurnsWhileHeld++;
            if (LoseLeaseAfterTurns > 0 && TurnsWhileHeld >= LoseLeaseAfterTurns)
                _handles[^1].Lose();
        }

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
