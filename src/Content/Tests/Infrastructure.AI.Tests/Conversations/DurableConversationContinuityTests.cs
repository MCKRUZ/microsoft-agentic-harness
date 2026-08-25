using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.AI.Common.Services.AI;
using Domain.AI.Budget;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The headline promise of issue #235, proven end to end: <em>"An Execution API bundle run can attach
/// to an existing conversation and see prior turns."</em>
/// </summary>
/// <remarks>
/// <para>
/// Everything else covering this feature is a unit test against a double — the command handler carries
/// the id, the executor carries the id and owner, the loop replays from a fake store. Each is worth
/// having and none of them touches the seam that actually has to work: a real SQLite transcript, a real
/// durable turn lease, and a second run reading what the first one wrote. Doubles agree with whatever
/// the code does; only the real store can disagree.
/// </para>
/// <para>
/// This drives <see cref="RunConversationCommandHandler"/> rather than booting a web host, because the
/// controller and executor layers above it are thin pass-throughs already covered by their own tests,
/// whereas the store, the lease and the loop are where continuity is actually decided.
/// </para>
/// </remarks>
public sealed class DurableConversationContinuityTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Stranger = "someone-else";

    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly SchemaInitializer<ConversationDbContext> _schema;
    private readonly EfCoreConversationStore _store;
    private readonly IConversationTurnLease _lease;

    /// <summary>
    /// Real time, deliberately — the one place in this suite where a frozen clock is wrong.
    /// </summary>
    /// <remarks>
    /// The durable lease discovers that a lease has been released by polling, and it waits between
    /// polls on this clock. Under a <c>FakeTimeProvider</c> nobody advances it, so the wait never
    /// elapses and a run queued behind another blocks forever — the serialisation test hangs rather
    /// than fails, which is a worse outcome than either. Nothing here asserts on a timestamp, so real
    /// time costs this suite nothing.
    /// </remarks>
    private readonly TimeProvider _clock = TimeProvider.System;

    public DurableConversationContinuityTests()
    {
        // The shared fixture, not a hand-rolled one. It disables connection pooling, which is what lets
        // the database file be deleted at the end of the test rather than staying open until the pool
        // is cleared — a hand-rolled factory in this same folder had to call ClearAllPools to compensate.
        _tempDir = Path.Combine(Path.GetTempPath(), $"durable-continuity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));
        _schema = new SchemaInitializer<ConversationDbContext>(_contextFactory);

        _store = new EfCoreConversationStore(
            _contextFactory, _clock, NullLogger<EfCoreConversationStore>.Instance, _schema);

        _lease = new SqliteConversationTurnLease(
            _contextFactory,
            Options.Create(new ConversationsConfig()),
            _clock,
            NullLogger<SqliteConversationTurnLease>.Instance,
            _schema);
    }

    [Fact]
    public async Task ASecondRun_SeesWhatTheFirstRunSaid()
    {
        // The feature. Run one asks and is answered; run two sends only its new message and must still
        // be dispatched with the earlier exchange in front of it.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var dispatched = new List<ExecuteAgentTurnCommand>();
        var handler = BuildHandler(dispatched);

        await handler.Handle(Durable(conversationId, "my name is Sam"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "what is my name?"), CancellationToken.None);

        dispatched.Should().HaveCount(2);

        dispatched[0].ConversationHistory.Should().BeEmpty(
            "the first run opened a conversation that did not exist yet");

        dispatched[1].ConversationHistory.Select(m => m.Text).Should().Equal(
            "my name is Sam", "answer 1");
    }

    [Fact]
    public async Task ASecondRun_SeesTheFirstRunsToolCallAsRealContent()
    {
        // #249 item 6, end to end against the real store: a tool called in run one must come back in
        // run two's replayed window as the real call/result the model actually produced, not text
        // narrating that it happened.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var dispatched = new List<ExecuteAgentTurnCommand>();
        var toolCall = new ToolCallRecord(
            "search", """{"query":"weather"}""", """{"result":"sunny"}""",
            DurationMs: 10, CallId: "call-1", RoundOrdinal: 0);
        var handler = BuildHandler(dispatched, toolCallsOnFirstTurn: [toolCall]);

        await handler.Handle(Durable(conversationId, "what's the weather?"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "and tomorrow?"), CancellationToken.None);

        dispatched.Should().HaveCount(2);
        var replayed = dispatched[1].ConversationHistory;

        var call = replayed.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should()
            .ContainSingle().Subject;
        call.CallId.Should().Be("call-1");
        call.Name.Should().Be("search");

        var result = replayed.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Should()
            .ContainSingle().Subject;
        result.CallId.Should().Be("call-1");
        result.Result.Should().Be("""{"result":"sunny"}""");
    }

    [Fact]
    public async Task DisablingReplayMidConversation_StopsReplayingAlreadyPersistedToolCalls()
    {
        // Security-review finding M-1: an operator's kill switch (AI:Conversations:ToolCallReplay:Enabled)
        // must stop replaying tool payloads ALREADY persisted from before it was flipped, not just stop
        // writing new ones — an incident-response control that only closes the write side leaves every
        // existing conversation's stored tool content shipping to the model on every later turn regardless.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var dispatched = new List<ExecuteAgentTurnCommand>();
        var toolCall = new ToolCallRecord(
            "search", """{"query":"weather"}""", """{"result":"sunny"}""",
            DurationMs: 10, CallId: "call-1", RoundOrdinal: 0);

        // Run one persists the tool call with replay enabled (today's default).
        var enabledHandler = BuildHandler(dispatched, toolCallsOnFirstTurn: [toolCall]);
        await enabledHandler.Handle(Durable(conversationId, "what's the weather?"), CancellationToken.None);

        // Run two — a fresh handler with the operator's kill switch flipped — must not replay the
        // already-persisted tool content, even though the record is still sitting in the store.
        var disabledHandler = BuildHandler(dispatched, replayToolCallsEnabled: false);
        await disabledHandler.Handle(Durable(conversationId, "and tomorrow?"), CancellationToken.None);

        dispatched.Should().HaveCount(2);
        var replayed = dispatched[1].ConversationHistory;

        replayed.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should().BeEmpty(
            "the kill switch must stop replaying already-persisted tool calls, not just stop writing new ones");
        replayed.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Should().BeEmpty();
        replayed.Select(m => m.Text).Should().Contain("answer 1",
            "the narrated text answer must still replay — only the structured tool-call content is gated");
    }

    [Fact]
    public async Task TheTranscriptSurvivesAsCompleteTurns_InOrder()
    {
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var handler = BuildHandler([]);

        await handler.Handle(Durable(conversationId, "first"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "second"), CancellationToken.None);

        var record = await _store.GetAsync(conversationId, Owner);

        record.Should().NotBeNull();

        // "answer 2" for the second run, not "answer 1": the double answers with the turn number it was
        // given, and turn numbering runs across the conversation rather than restarting each run.
        record!.Messages.Select(m => m.Content).Should().Equal(
            "first", "answer 1", "second", "answer 2");
    }

    [Fact]
    public async Task TurnNumbering_ContinuesAcrossRuns()
    {
        // Against the real store, because this is the number per-turn observability rows are keyed by
        // together with the conversation id. Restarting at 1 each run makes run 2's opening turn collide
        // with run 1's and overwrite it (issue #255) — a collision no in-memory double would show.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var dispatched = new List<ExecuteAgentTurnCommand>();
        var handler = BuildHandler(dispatched);

        await handler.Handle(Durable(conversationId, "one"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "two"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "three"), CancellationToken.None);

        dispatched.Select(d => d.TurnNumber).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task AnotherUsersConversation_IsRefusedByTheRealStore()
    {
        // Ownership is enforced inside the store, so it has to hold against the real one — a mocked
        // store enforces nothing, and every other test of this path uses a double.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        await BuildHandler([]).Handle(Durable(conversationId, "mine"), CancellationToken.None);

        var intruder = BuildHandler([]);
        var act = () => intruder.Handle(
            Durable(conversationId, "let me read that") with { ConversationOwnerId = Stranger },
            CancellationToken.None);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();

        var record = await _store.GetAsync(conversationId, Owner);
        record!.Messages.Should().HaveCount(2, "the refused run must not have written or replaced anything");
    }

    [Fact]
    public async Task TheReplayWindowIsBounded_EvenThoughTheTranscriptIsNot()
    {
        // Prompt growth is what the window exists to stop. The stored transcript keeps everything; only
        // the tail is replayed.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var dispatched = new List<ExecuteAgentTurnCommand>();

        // Window of 2: the run before last contributes nothing to the final dispatch.
        var handler = BuildHandler(dispatched, maxHistoryMessages: 2);

        await handler.Handle(Durable(conversationId, "one"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "two"), CancellationToken.None);
        await handler.Handle(Durable(conversationId, "three"), CancellationToken.None);

        // "answer 2" is the second run's reply — the double echoes the turn number it was handed, and
        // that number now continues across runs rather than restarting.
        dispatched[2].ConversationHistory.Select(m => m.Text).Should().Equal(
            "two", "answer 2");

        var record = await _store.GetAsync(conversationId, Owner);
        record!.Messages.Should().HaveCount(6, "the transcript keeps every turn the window does not replay");
    }

    [Fact]
    public async Task ASecondRunOnTheSameConversation_WaitsForTheFirstToFinishItsTurn()
    {
        // Turn serialisation against the real durable lease. The second run is admitted only after the
        // first has released, so its dispatch sees the first run's exchange rather than racing it.
        var conversationId = $"conv-{Guid.NewGuid():N}";
        var firstTurnEntered = new TaskCompletionSource();
        var releaseFirstTurn = new TaskCompletionSource();
        var dispatched = new List<ExecuteAgentTurnCommand>();

        var blocking = BuildHandler(dispatched, onTurn: async () =>
        {
            firstTurnEntered.TrySetResult();
            await releaseFirstTurn.Task;
        });

        var first = blocking.Handle(Durable(conversationId, "hold the lease"), CancellationToken.None);
        await firstTurnEntered.Task;

        var second = BuildHandler(dispatched).Handle(
            Durable(conversationId, "queued behind it"), CancellationToken.None);

        // The lease polls, so give the second run a real opportunity to barge in if it can.
        var bargedIn = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(750)));
        bargedIn.Should().NotBeSameAs(second, "the second run must wait for the first run's lease");

        releaseFirstTurn.SetResult();
        await first;
        await second;

        dispatched.Should().HaveCount(2);
        dispatched[1].ConversationHistory.Select(m => m.Text).Should().Equal(
            "hold the lease", "answer 1");
    }

    // -- Helpers --

    private static RunConversationCommand Durable(string conversationId, string message) => new()
    {
        AgentName = "TestAgent",
        ConversationId = conversationId,
        ConversationOwnerId = Owner,
        UserMessages = [message],
        MaxTurns = 10
    };

    /// <summary>
    /// Builds a handler over the REAL store and lease, with only the model turn itself faked — that is
    /// the one collaborator a test cannot have.
    /// </summary>
    private RunConversationCommandHandler BuildHandler(
        List<ExecuteAgentTurnCommand> dispatched,
        int maxHistoryMessages = 50,
        Func<Task>? onTurn = null,
        IReadOnlyList<ToolCallRecord>? toolCallsOnFirstTurn = null,
        bool replayToolCallsEnabled = true)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async (ExecuteAgentTurnCommand cmd, CancellationToken ct) =>
            {
                lock (dispatched)
                    dispatched.Add(cmd);

                if (onTurn is not null)
                    await onTurn();

                var response = $"answer {cmd.TurnNumber}";
                return new AgentTurnResult
                {
                    Success = true,
                    Response = response,
                    UpdatedHistory =
                    [
                        .. cmd.ConversationHistory,
                        new ChatMessage(ChatRole.User, cmd.UserMessage),
                        new ChatMessage(ChatRole.Assistant, response)
                    ],
                    ToolCalls = cmd.TurnNumber == 1 ? toolCallsOnFirstTurn ?? [] : []
                };
            });

        var budget = new Mock<IConversationBudgetTracker>();
        budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        var observability = new Mock<IObservabilityStore>().Object;

        var toolCallReplayTreatment = new Mock<IToolCallReplayTreatment>();
        toolCallReplayTreatment.Setup(t => t.Enabled).Returns(replayToolCallsEnabled);

        return new RunConversationCommandHandler(
            mediator.Object,
            new Mock<IAgentConversationCache>().Object,
            budget.Object,
            observability,
            // The real recorder over the real store this fixture uses, so continuity across runs is
            // exercised end to end rather than stubbed.
            new ConversationTelemetryRecorder(
                observability, _store, NullLogger<ConversationTelemetryRecorder>.Instance),
            _store,
            _lease,
            Options.Create(new ConversationsConfig { MaxHistoryMessages = maxHistoryMessages }),
            toolCallReplayTreatment.Object,
            NullLogger<RunConversationCommandHandler>.Instance);
    }

    public void Dispose()
    {
        // Takes the WAL and shared-memory sidecars with it, same as the other SQLite suites here.
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
