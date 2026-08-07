using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services.AI;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Budget;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Tests.Telemetry;
using Xunit;
using Application.AI.Common.Models.Conversations;

namespace Presentation.AgentHub.Tests.AgUi;

public sealed class AgUiRunHandlerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ClaimsPrincipal MakeUser(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], "test"));

    /// <summary>
    /// A principal carrying <c>sub</c> and nothing else — the shape of a non-Entra OIDC token.
    /// </summary>
    private static ClaimsPrincipal MakeSubOnlyUser(string sub) =>
        new(new ClaimsIdentity([new Claim("sub", sub)], "test"));

    private static RunAgentInput MakeInput(string threadId, string userContent) =>
        MakeInput(threadId, userContent, Guid.NewGuid().ToString());

    private static RunAgentInput MakeInput(string threadId, string userContent, string userMessageId) =>
        new()
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages =
            [
                new AgUiMessage { Id = userMessageId, Role = "user", Content = userContent }
            ]
        };

    private static ConversationRecord MakeRecord(string id, string userId, string agentName = "test-agent") =>
        new(id, agentName, userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [], null, null);

    private static AgentTurnResult MakeSuccessResult(string response) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = [new ChatMessage(ChatRole.Assistant, response)]
        };

    private static AgentTurnResult MakeSuccessResultWithUsage(
        string response, int inputTokens = 150, int outputTokens = 80,
        int cacheRead = 40, int cacheWrite = 10, decimal costUsd = 0.003m,
        string model = "gpt-4o", List<string>? tools = null) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = [new ChatMessage(ChatRole.Assistant, response)],
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            CostUsd = costUsd,
            Model = model,
            ToolsInvoked = tools ?? [],
        };

    private static AgentTurnResult MakeFailureResult(string error) =>
        new()
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = error
        };

    private static AgentTurnResult MakeConfigFailureResult(string error) =>
        new()
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = error,
            ErrorKind = AgentTurnErrorKind.Configuration
        };

    private static (Mock<IMediator> Mediator, Mock<IConversationStore> Store) SetupSuccessfulTurn(
        string threadId, string userId)
    {
        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeSuccessResult("ok"));
        return (mediator, store);
    }

    private static (Mock<IMediator> Mediator, Mock<IConversationStore> Store) SetupFailingTurn(
        string threadId, string userId, AgentTurnResult failure)
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(failure);
        return (mediator, store);
    }

    [Fact]
    public async Task HandleRunAsync_CompletedRun_CountsTheRunAndGivesTheCountBack()
    {
        // What this transport can honestly count is a run. It used to increment the shared
        // active-sessions gauge when a session was opened, and there is no moment here that could ever
        // decrement it — a stateless request leaves the conversation's session open for the next one —
        // so its contribution was "conversations this transport has ever started", climbing forever
        // and summed with two other transports answering two other questions (issue #289).
        //
        // Both halves are asserted deliberately. A gauge nobody touches also nets to zero, so the count
        // of measurements is what proves the instrument was reached at all; every unit test over this
        // path passed while the leak was live precisely because none of them could see it.
        const string threadId = "conv-gauge";
        const string userId = "user-1";

        var (mediator, store) = SetupSuccessfulTurn(threadId, userId);
        var handler = BuildHandler(mediator, store);

        using var probe = new GaugeProbe(OrchestrationConventions.RunsActive);
        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        probe.Measurements.Should().Be(2, "the run must be counted up when it starts and down when it ends");
        probe.Net.Should().Be(0, "a finished run is not a run in flight");
    }

    [Fact]
    public async Task HandleRunAsync_TurnFails_StillGivesTheRunCountBack()
    {
        // The decrement lives in a finally for this reason. An up-down counter that is skipped on the
        // failure path does not merely under-report — it never recovers, because nothing ever
        // subtracts the run that errored, and the floor it leaves behind is permanent.
        const string threadId = "conv-gauge-fail";
        const string userId = "user-1";

        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeFailureResult("boom"));
        var handler = BuildHandler(mediator, store);

        using var probe = new GaugeProbe(OrchestrationConventions.RunsActive);
        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        probe.Measurements.Should().Be(2);
        probe.Net.Should().Be(0);
    }

    [Fact]
    public async Task HandleRunAsync_ConversationChangedWhileQueued_DispatchesAgainstTheRereadRecord()
    {
        // The record is loaded before the turn lease is taken, so by the time this turn is exclusive
        // the turn it waited behind — possibly in another host — may already have appended to the
        // transcript. Dispatching from the pre-lease snapshot numbers this turn as though that never
        // happened, and calls the model with settings that have since been replaced.
        const string threadId = "conv-reread";
        const string userId = "user-1";

        var stale = MakeRecord(threadId, userId);

        // The turn that landed while this one queued wrote BOTH halves of what it produces: the
        // exchange, and the conversation's running telemetry. A fixture that advanced only the
        // transcript would describe a state no completed turn can leave behind.
        var fresh = stale with
        {
            Messages =
            [
                new ConversationMessage(Guid.NewGuid(), MessageRole.User, "earlier", DateTimeOffset.UtcNow),
                new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "answered", DateTimeOffset.UtcNow),
            ],
            Telemetry = TelemetryAccumulator.Zero with { TurnCount = 1 },
        };

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();

        // First read is the one before the lease; every read after it sees the newer transcript.
        store.SetupSequence(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(stale)
             .ReturnsAsync(fresh);
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        ExecuteAgentTurnCommand? dispatched = null;
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .Callback<object, CancellationToken>((c, _) => dispatched = (ExecuteAgentTurnCommand)c)
                .ReturnsAsync(MakeSuccessResult("ok"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        dispatched.Should().NotBeNull();
        dispatched!.TurnNumber.Should().Be(fresh.Telemetry!.TurnCount + 1,
            "the turn must be numbered from the conversation as it stands once the lease is held, not "
            + "from the snapshot taken before waiting for it — the stale record would number this 1");
    }

    [Fact]
    public async Task HandleRunAsync_LeaseLostMidTurn_StopsTheTurnAndSaysWhy()
    {
        // The lease's expiry means a stalled host can have its lease taken while its turn is still
        // running. If that is not linked into the turn's token, the losing host keeps writing to a
        // transcript another host is now writing to — the concurrent turn the lease exists to
        // prevent, reintroduced by the mechanism meant to stop it. Reported distinctly from a client
        // disconnect, because both arrive as a cancellation and only one of them is routine.
        const string threadId = "conv-stolen";
        const string userId = "user-1";

        var logger = new CapturingLogger<AgUiRunHandler>();
        var lease = new ControllableTurnLease();
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        // Another host takes the lease while the model call is in flight. The turn aborts only if the
        // token it was handed is the linked one — stealing the lease and throwing unconditionally
        // would prove the error message and nothing about the wiring that produces it.
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .Returns<object, CancellationToken>((_, dispatchToken) =>
                {
                    lease.Steal();
                    dispatchToken.ThrowIfCancellationRequested();
                    return Task.FromResult(MakeSuccessResult("the turn was never stopped"));
                });

        var handler = BuildHandler(mediator, store, turnLease: lease, logger: logger);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms))
            .Should().Be("This conversation was continued elsewhere; the turn was stopped.");
        logger.Logged(LogLevel.Warning, "was lost").Should().BeTrue(
            "the other half of this rule is that an ordinary disconnect must NOT log this");
        lease.Released.Should().BeTrue("the lease must be released even when the turn ends this way");
    }

    [Fact]
    public async Task HandleRunAsync_ClientDisconnectsAsTheLeaseIsLost_ReportsTheDisconnect()
    {
        // Both can be true at once, and then the disconnect is the honest explanation.
        //
        // Asserted on the log rather than on the stream, because the stream cannot tell the two
        // apart: the client is gone, so the "continued elsewhere" event is written to an already
        // cancelled token, fails, and is swallowed either way. Checking the frames here would pass
        // with the rule wrong — measured, not assumed. What survives a disconnect is the log line an
        // operator later reads, and reporting a lost lease there for an ordinary disconnect sends
        // them looking for a second host that was never involved.
        const string threadId = "conv-both";
        const string userId = "user-1";

        var logger = new CapturingLogger<AgUiRunHandler>();
        var lease = new ControllableTurnLease();
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        using var caller = new CancellationTokenSource();
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .Returns<object, CancellationToken>((_, dispatchToken) =>
                {
                    lease.Steal();
                    caller.Cancel();
                    dispatchToken.ThrowIfCancellationRequested();
                    return Task.FromResult(MakeSuccessResult("the turn was never stopped"));
                });

        var handler = BuildHandler(mediator, store, turnLease: lease, logger: logger);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId), caller.Token);

        logger.Logged(LogLevel.Warning, "was lost").Should().BeFalse(
            "the client disconnected, which explains the cancellation without invoking a second host");
        ParseSseFrames(ms).Select(EventType).Should().NotContain(AgUiEventType.RunError);
        lease.Released.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRunAsync_ClientDisconnects_EndsQuietlyRatherThanReportingALostLease()
    {
        // The control for the test above. A disconnect and a stolen lease both surface as a
        // cancellation, so a handler that reported "continued elsewhere" for either would look
        // correct in the stolen case while lying in the ordinary one.
        const string threadId = "conv-disconnect";
        const string userId = "user-1";

        var lease = new ControllableTurnLease();
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        // Cancelled, but the lease was never lost.
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

        var handler = BuildHandler(mediator, store, turnLease: lease);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        ParseSseFrames(ms).Select(EventType).Should().NotContain(AgUiEventType.RunError);
        lease.Released.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRunAsync_ConversationDeletedWhileQueued_ReportsNotFoundInsteadOfDispatching()
    {
        // The conversation existed when this run started and was gone by the time it held the lease.
        // Dispatching anyway spends a model call on a transcript that no longer exists and then fails
        // on the append, reported as an unexpected error rather than as what actually happened.
        const string threadId = "conv-deleted";
        const string userId = "user-1";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();

        store.SetupSequence(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId))
             .ReturnsAsync((ConversationRecord?)null);

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms)).Should().Be("Conversation not found.");
        mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string RunErrorMessage(IEnumerable<JsonDocument> frames) =>
        frames.First(f => EventType(f) == AgUiEventType.RunError)
              .RootElement.GetProperty("message").GetString()!;

    /// <summary>
    /// Parses SSE frames from a MemoryStream and returns the deserialized event objects.
    /// Each frame has the form <c>data: {json}\n\n</c>.
    /// </summary>
    private static List<JsonDocument> ParseSseFrames(MemoryStream stream)
    {
        stream.Position = 0;
        var raw = Encoding.UTF8.GetString(stream.ToArray());
        var frames = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var docs = new List<JsonDocument>();

        foreach (var frame in frames)
        {
            var line = frame.Trim();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var json = line["data: ".Length..];
            docs.Add(JsonDocument.Parse(json));
        }

        return docs;
    }

    private static string EventType(JsonDocument doc) =>
        doc.RootElement.GetProperty("type").GetString()!;

    private static AgUiRunHandler BuildHandler(
        Mock<IMediator> mediator,
        Mock<IConversationStore> store,
        Mock<IObservabilityStore>? observability = null,
        string environmentName = "Development",
        Mock<IConversationBudgetTracker>? budget = null,
        IConversationTurnLease? turnLease = null,
        ILogger<AgUiRunHandler>? logger = null)
    {
        if (observability is null)
        {
            observability = new Mock<IObservabilityStore>();
            observability.Setup(o => o.StartSessionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
        }

        if (budget is null)
        {
            // Budget disabled by default — most tests don't exercise the conversation budget.
            budget = new Mock<IConversationBudgetTracker>();
            budget
                .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ConversationBudgetStatus.Disabled);
        }

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        return new AgUiRunHandler(
            mediator.Object,
            store.Object,
            observability.Object,
            // The real recorder over the mocked stores, for the same reason as the lease below: a mocked
            // recorder would make these tests assertions about an interface rather than about what
            // actually reaches the observability row, which is the whole subject.
            new ConversationTelemetryRecorder(
                observability.Object, store.Object, NullLogger<ConversationTelemetryRecorder>.Instance),
            // The real in-process lease by default, not a mock: a mocked one returns a null handle,
            // and every test below would then run a turn that never leased anything.
            turnLease ?? new InProcessConversationTurnLease(),
            new AgUiEventWriterAccessor(),
            budget.Object,
            environment.Object,
            logger ?? NullLogger<AgUiRunHandler>.Instance);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleRunAsync_ConversationNotFound_EmitsRunStartedThenRunError()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ConversationRecord?)null);

        var handler = BuildHandler(mediator, store);
        var input = MakeInput("no-such-thread", "hello");
        var user = MakeUser("user-1");

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        frames.Should().HaveCountGreaterThanOrEqualTo(2);
        EventType(frames[0]).Should().Be(AgUiEventType.RunStarted);
        EventType(frames[1]).Should().Be(AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);

        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_SubOnlyToken_IsRecognisedAsTheOwner()
    {
        // The drift this covers, behaviourally rather than structurally: GetCallerId used to be a
        // hand-rolled copy of the shared ladder whose comment claimed it "mirrors" the authority. It
        // stopped mirroring it when the shared ladder learned to accept "sub", so a non-Entra OIDC
        // caller who owned the conversation everywhere else in the harness was rejected here.
        // CallerIdentityResolutionBoundaryTests stops a fourth ladder appearing; this asserts the
        // outcome that mattered — the owner is let in.
        const string threadId = "conv-sub-owner";
        const string sub = "sub-only-owner";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        // Keyed on `sub` as the caller, which is the assertion: the handler has to resolve a sub-only
        // token to that id and hand it to the store. If it resolved to anything else these setups
        // would not match and the run would fail on a null record instead of succeeding.
        store.Setup(s => s.GetAsync(threadId, sub, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, sub));
        store.Setup(s => s.GetHistoryForDispatch(threadId, sub, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, sub, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("Hello."));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeSubOnlyUser(sub));

        var frames = ParseSseFrames(ms);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunError,
            "a sub-only owner must not be treated as an intruder");
        frames.Should().Contain(f => EventType(f) == AgUiEventType.RunFinished);
    }

    [Fact]
    public async Task HandleRunAsync_SubOnlyToken_StillCannotReadAnotherOwnersThread()
    {
        // The other half: accepting "sub" must widen who is recognised, never who is authorised.
        // Without this, "resolve sub to the owner id" and "resolve sub to whatever the record says"
        // look identical from the test above.
        const string threadId = "conv-sub-intruder";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        // The store refuses, because ownership is now its decision rather than the handler's. Stubbed
        // to throw exactly what the real implementations throw: what this test proves is that the
        // handler turns that refusal into a RunError and dispatches nothing — not that it re-derives
        // the ownership rule itself, which it deliberately no longer does.
        store.Setup(s => s.GetAsync(threadId, "someone-else", It.IsAny<CancellationToken>()))
             .ThrowsAsync(new ConversationAccessDeniedException());

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeSubOnlyUser("someone-else"));

        var frames = ParseSseFrames(ms);
        // The message, not merely the presence of an error. This stream reports refusals and faults
        // through the same event, so asserting only "a RunError happened" would still pass if the
        // refusal fell through to the generic handler and reached the client as "an error occurred".
        RunErrorMessage(frames).Should().Be("Access denied.");
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);
        mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_WrongUser_EmitsRunError()
    {
        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        // Refused by the store, which is where ownership now lives. See the sub-only intruder test
        // above for why this is stubbed as a throw rather than as a record the handler must reject.
        store.Setup(s => s.GetAsync("conv-1", "different-user", It.IsAny<CancellationToken>()))
             .ThrowsAsync(new ConversationAccessDeniedException());

        var handler = BuildHandler(mediator, store);
        var input = MakeInput("conv-1", "hello");
        var intruder = MakeUser("different-user");

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, intruder);

        var frames = ParseSseFrames(ms);
        RunErrorMessage(frames).Should().Be("Access denied.",
            "a refusal must reach the client as a refusal, not as a generic failure");
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);

        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_ConversationBudgetExhausted_EmitsAssistantMessageThenRunFinished_NoDispatch()
    {
        const string threadId = "conv-budget";
        const string userId = "user-1";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<ConversationMessage>());

        var budget = new Mock<IConversationBudgetTracker>();
        budget
            .Setup(b => b.GetStatusAsync(threadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationBudgetStatus(true, 100, 100));

        var handler = BuildHandler(mediator, store, budget: budget);
        var input = MakeInput(threadId, "hello");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        // Graceful: a normal assistant text message + RunFinished, never a RunError.
        frames.Should().Contain(f => EventType(f) == AgUiEventType.TextMessageContent);
        frames.Should().Contain(f => EventType(f) == AgUiEventType.RunFinished);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunError);

        mediator.Verify(
            m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never,
            "no LLM turn should be dispatched once the conversation budget is exhausted");
    }

    [Fact]
    public async Task HandleRunAsync_ConfigurationError_InDevelopment_SurfacesActionableMessage()
    {
        const string threadId = "conv-cfg-dev";
        const string userId = "user-cfg";
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";

        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeConfigFailureResult(actionable));
        var handler = BuildHandler(mediator, store, environmentName: "Development");

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms)).Should().Be(actionable);
    }

    [Fact]
    public async Task HandleRunAsync_ConfigurationError_InProduction_StaysGeneric()
    {
        const string threadId = "conv-cfg-prod";
        const string userId = "user-cfg";
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";

        var (mediator, store) = SetupFailingTurn(threadId, userId, MakeConfigFailureResult(actionable));
        var handler = BuildHandler(mediator, store, environmentName: "Production");

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        RunErrorMessage(ParseSseFrames(ms)).Should().Be("The agent was unable to process your request.");
    }

    [Fact]
    public async Task HandleRunAsync_CancelledTurn_PropagatesCancellation_NoRunError()
    {
        // A cancelled turn (e.g. caller disconnect) is routine — it funnels into the
        // handler's central cancellation sink (no event emitted) rather than surfacing a
        // user-facing RunError. The run aborts gracefully without a RunError or RunFinished.
        const string threadId = "conv-cancel";
        const string userId = "user-cancel";
        var cancelled = new AgentTurnResult
        {
            Success = false,
            Response = string.Empty,
            UpdatedHistory = [],
            Error = "cancelled",
            ErrorKind = AgentTurnErrorKind.Cancelled,
        };

        var (mediator, store) = SetupFailingTurn(threadId, userId, cancelled);
        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        var act = () => handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        await act.Should().NotThrowAsync();
        var frames = ParseSseFrames(ms);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);
    }

    [Fact]
    public async Task HandleRunAsync_HappyPath_EmitsFullEventSequence()
    {
        const string threadId = "conv-happy";
        const string userId = "user-happy";
        const string agentResponse = "Hello! I am your AI assistant.";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult(agentResponse));

        var budget = new Mock<IConversationBudgetTracker>();
        budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        var handler = BuildHandler(mediator, store, budget: budget);
        var input = MakeInput(threadId, "Hi there");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        var types = frames.Select(EventType).ToList();

        // Required ordering
        types[0].Should().Be(AgUiEventType.RunStarted);
        types.Should().Contain(AgUiEventType.TextMessageStart);
        types.Should().Contain(AgUiEventType.TextMessageContent);
        types.Should().Contain(AgUiEventType.TextMessageEnd);
        types.Last().Should().Be(AgUiEventType.RunFinished);

        // TEXT_MESSAGE_START must precede TEXT_MESSAGE_END
        var startIdx = types.IndexOf(AgUiEventType.TextMessageStart);
        var endIdx = types.LastIndexOf(AgUiEventType.TextMessageEnd);
        startIdx.Should().BeLessThan(endIdx);

        // Reconstructed delta content must equal the full response
        var messageId = frames.First(f => EventType(f) == AgUiEventType.TextMessageStart)
                              .RootElement.GetProperty("messageId").GetString();
        var reconstructed = string.Concat(
            frames.Where(f => EventType(f) == AgUiEventType.TextMessageContent)
                  .Select(f => f.RootElement.GetProperty("delta").GetString()));
        reconstructed.Should().Be(agentResponse);

        // All content events share the same messageId as the start event
        frames.Where(f => EventType(f) == AgUiEventType.TextMessageContent)
              .All(f => f.RootElement.GetProperty("messageId").GetString() == messageId)
              .Should().BeTrue();

        // A successful turn folds its usage into the conversation-lifetime budget.
        budget.Verify(
            b => b.RecordUsageAsync(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        // Conversation persistence: user msg + assistant msg both appended
        store.Verify(s => s.AppendMessageAsync(
            threadId, userId,
            It.Is<ConversationMessage>(m => m.Role == MessageRole.User),
            It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.AppendMessageAsync(
            threadId, userId,
            It.Is<ConversationMessage>(m => m.Role == MessageRole.Assistant),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRunAsync_ClientSuppliesUserMessageId_PersistsUserMessageUnderThatId()
    {
        const string threadId = "conv-clientid";
        const string userId = "user-clientid";
        var clientUserId = Guid.NewGuid();

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, string, ConversationMessage, CancellationToken>((_, _, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi", clientUserId.ToString()), new AgUiEventWriter(ms), MakeUser(userId));

        var userMsg = appended.Single(m => m.Role == MessageRole.User);
        userMsg.Id.Should().Be(clientUserId, "the server must persist the user message under the client-supplied id so retry/edit can reference it");
    }

    [Fact]
    public async Task HandleRunAsync_ClientSuppliesNonGuidId_GeneratesServerSideId()
    {
        const string threadId = "conv-badid";
        const string userId = "user-badid";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, string, ConversationMessage, CancellationToken>((_, _, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(
            MakeInput(threadId, "Hi", "not-a-guid"), new AgUiEventWriter(ms), MakeUser(userId));

        var userMsg = appended.Single(m => m.Role == MessageRole.User);
        userMsg.Id.Should().NotBe(Guid.Empty, "a non-GUID client id must fall back to a server-generated id");
    }

    [Fact]
    public async Task HandleRunAsync_AssistantMessage_StreamedIdMatchesPersistedId()
    {
        const string threadId = "conv-asstid";
        const string userId = "user-asstid";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var appended = new List<ConversationMessage>();

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeRecord(threadId, userId));
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Callback<string, string, ConversationMessage, CancellationToken>((_, _, m, _) => appended.Add(m))
             .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResult("assistant reply"));

        var handler = BuildHandler(mediator, store);

        using var ms = new MemoryStream();
        await handler.HandleRunAsync(MakeInput(threadId, "Hi"), new AgUiEventWriter(ms), MakeUser(userId));

        var frames = ParseSseFrames(ms);
        var streamedId = frames.First(f => EventType(f) == AgUiEventType.TextMessageStart)
                               .RootElement.GetProperty("messageId").GetString();
        var persistedAssistant = appended.Single(m => m.Role == MessageRole.Assistant);

        persistedAssistant.Id.ToString().Should().Be(
            streamedId, "the streamed assistant message id must equal the persisted id so retry-from-assistant resolves");
    }

    [Fact]
    public async Task HandleRunAsync_AgentFails_EmitsRunErrorNoTextEvents()
    {
        const string threadId = "conv-fail";
        const string userId = "user-fail";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeFailureResult("Internal agent error."));

        var handler = BuildHandler(mediator, store);
        var input = MakeInput(threadId, "Do something");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        var types = frames.Select(EventType).ToList();

        types[0].Should().Be(AgUiEventType.RunStarted);
        types.Should().Contain(AgUiEventType.RunError);
        types.Should().NotContain(AgUiEventType.TextMessageStart);
        types.Should().NotContain(AgUiEventType.TextMessageContent);
        types.Should().NotContain(AgUiEventType.TextMessageEnd);
        types.Should().NotContain(AgUiEventType.RunFinished);
    }

    [Fact]
    public async Task HandleRunAsync_NoUserMessage_EmitsRunError()
    {
        const string threadId = "conv-nomsg";
        const string userId = "user-nomsg";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var record = MakeRecord(threadId, userId);
        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        var handler = BuildHandler(mediator, store);
        var input = new RunAgentInput
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages = [new AgUiMessage { Id = "1", Role = "assistant", Content = "hi" }]
        };
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        var frames = ParseSseFrames(ms);
        frames.Should().Contain(f => EventType(f) == AgUiEventType.RunError);
        frames.Should().NotContain(f => EventType(f) == AgUiEventType.RunFinished);
        mediator.Verify(m => m.Send(It.IsAny<IRequest<AgentTurnResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRunAsync_FirstTurn_CreatesSessionAndPersistsMetrics()
    {
        const string threadId = "conv-telemetry-1";
        const string userId = "user-tel-1";
        const string agentResponse = "Here are your tools.";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();
        var sessionId = Guid.NewGuid();

        var record = MakeRecord(threadId, userId);
        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, userId, It.IsAny<Guid>(), It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        observability.Setup(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(sessionId);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage(agentResponse, inputTokens: 100, outputTokens: 50, costUsd: 0.01m));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "What tools do you have?");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Session was started in the observability store
        observability.Verify(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()), Times.Once);

        // Session metrics were updated with non-zero values
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            1, 0, 0,
            100, 50,
            It.IsAny<int>(), It.IsAny<int>(),
            0.01m,
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry was persisted to conversation store (twice: once for Zero on session start, once after turn)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, userId, sessionId,
            It.IsAny<TelemetryAccumulator>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HandleRunAsync_SecondTurn_ReusesSessionAndAccumulatesMetrics()
    {
        const string threadId = "conv-telemetry-2";
        const string userId = "user-tel-2";
        var sessionId = Guid.NewGuid();

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();

        var existingTelemetry = new TelemetryAccumulator(1, 0, 100, 50, 40, 10, 0.01m);
        var record = new ConversationRecord(
            threadId, "test-agent", userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ConversationMessage(Guid.NewGuid(), MessageRole.User, "first msg", DateTimeOffset.UtcNow),
             new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "first reply", DateTimeOffset.UtcNow)],
            "first msg", null, sessionId, existingTelemetry);

        store.Setup(s => s.GetAsync(threadId, userId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record.Messages);
        store.Setup(s => s.AppendMessageAsync(threadId, userId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, userId, sessionId, It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage("second reply", inputTokens: 200, outputTokens: 100,
                    cacheRead: 60, cacheWrite: 20, costUsd: 0.02m, tools: ["file_system"]));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "Use a tool please");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Should NOT start a new session
        observability.Verify(o => o.StartSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Session metrics should be accumulated (turn1 + turn2)
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            2, 1, 0,
            300, 150, 100, 30,
            0.03m,
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry persisted once (only after turn, no session start)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, userId, sessionId,
            It.Is<TelemetryAccumulator>(t => t.TurnCount == 2 && t.ToolCallCount == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
