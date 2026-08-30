using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
using Application.AI.Common.Services.AI;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Observability.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.AI.Budget;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Presentation.AgentHub.Tests.Telemetry;
using Xunit;
using Application.AI.Common.Models.Conversations;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ConversationOrchestrator"/> covering conversation lifecycle,
/// turn dispatch, ownership validation, error handling, and session tracking.
/// </summary>
public class ConversationOrchestratorTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationStore> _store = new();
    private readonly Mock<ISessionHealthTracker> _healthTracker = new();
    private readonly Mock<IObservabilityStore> _obsStore = new();
    private readonly Mock<IConnectionTracker> _connectionTracker = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly Mock<IToolCallReplayTreatment> _toolCallReplayTreatment = new();
    // The real in-process lease rather than a mock: a mocked lease would hand back a null handle and
    // every test here would then be exercising a turn that never took one. Replaced per-test where a
    // lost lease has to be simulated, which the in-process one never does.
    private IConversationTurnLease _turnLease = new InProcessConversationTurnLease();
    private readonly AgentHubConfig _config = new() { MaxHistoryMessages = 20 };

    public ConversationOrchestratorTests()
    {
        // Budget disabled by default — most tests don't exercise the conversation budget.
        _budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        // Enabled by default — most tests don't exercise the tool-call replay kill switch.
        _toolCallReplayTreatment.Setup(t => t.Enabled).Returns(true);
        // Explicit, because an unconfigured Moq int property returns 0 — and a zero limit is not "no
        // limit", it drops every replayed tool call.
        _toolCallReplayTreatment.Setup(t => t.MaxCallsPerTurn).Returns(32);
        _toolCallReplayTreatment.Setup(t => t.MaxReplayedChars).Returns(65536);
    }

    private ConversationOrchestrator CreateOrchestrator(string environmentName = "Development")
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return new(
            _mediator.Object,
            _store.Object,
            _turnLease,
            _healthTracker.Object,
            _obsStore.Object,
            // The real recorder over the mocked stores, not a mocked recorder. A mock would make every
            // assertion below about the recorder's interface rather than about what actually reaches the
            // observability row — and the defect this replaced was entirely about what reached that row.
            new ConversationTelemetryRecorder(
                _obsStore.Object, _store.Object, NullLogger<ConversationTelemetryRecorder>.Instance),
            _connectionTracker.Object,
            _budget.Object,
            _toolCallReplayTreatment.Object,
            Options.Create(_config),
            environment.Object,
            NullLogger<ConversationOrchestrator>.Instance);
    }

    // ── StartConversation ────────────────────────────────────────────────

    [Fact]
    public async Task StartConversation_NewConversation_CreatesRecord()
    {
        var expected = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync(It.IsAny<string>(), "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);
        _store.Setup(s => s.CreateAsync("agent", "user1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        var orchestrator = CreateOrchestrator();
        var (record, history) = await orchestrator.StartConversationAsync("conn1", "agent", null, "user1", CancellationToken.None);

        record.Should().Be(expected);
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task StartConversation_ExistingConversation_ReturnsExistingRecord()
    {
        // A supplied id now goes through the store's atomic open. The read-then-create this replaced
        // was a race: CreateAsync REPLACES, so two clients reconnecting on the same id could both see
        // it absent and the loser's create would delete the winner's transcript.
        var existing = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetOrCreateAsync("agent", "user1", "c1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        var orchestrator = CreateOrchestrator();
        var (record, _) = await orchestrator.StartConversationAsync("conn1", "agent", "c1", "user1", CancellationToken.None);

        record.Should().Be(existing);
        _store.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "a caller-supplied id must never reach the replacing create path");
    }

    [Fact]
    public async Task StartConversation_WrongOwner_ThrowsUnauthorizedAccessException()
    {
        // Keyed on the attacker, because the store is the thing that decides now: it is asked as the
        // caller who actually made the request, and refuses. What the orchestrator still owes is that
        // it asks with the real caller and lets the refusal out — not that it re-derives the rule.
        //
        // Stubbed explicitly, and it has to be: a MOCKED store enforces nothing, so without this the
        // test would assert an intruder is refused while nothing in the run does any refusing.
        _store.Setup(s => s.GetOrCreateAsync("agent", "attacker", "c1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationAccessDeniedException());

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.StartConversationAsync("conn1", "agent", "c1", "attacker", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _store.Verify(
            s => s.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a refused start must not fall through to creating a fresh conversation under that id");
    }

    // ── SetSettings ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetSettings_ValidOwner_UpdatesSettings()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.UpdateSettingsAsync("c1", "user1", It.IsAny<ConversationSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        var settings = new ConversationSettings("gpt-4o", 0.7f, null);

        await orchestrator.SetSettingsAsync("c1", settings, "user1", CancellationToken.None);

        _store.Verify(s => s.UpdateSettingsAsync("c1", "user1", settings, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetSettings_NotFound_ThrowsInvalidOperationException()
    {
        _store.Setup(s => s.GetAsync("missing", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.SetSettingsAsync("missing", new ConversationSettings(null, null, null), "user1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── SendMessage ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_Success_ReturnsTurnOutcomeWithResponse()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        // Simulate the handler streaming deltas through the ambient sink the orchestrator
        // attaches for the duration of the dispatch.
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var sink = AgentTurnStreamSink.Current;
                if (sink is not null)
                {
                    await sink.EmitAsync("Hello ", CancellationToken.None);
                    await sink.EmitAsync("from agent", CancellationToken.None);
                }
                return new AgentTurnResult
                {
                    Success = true,
                    Response = "Hello from agent",
                    UpdatedHistory = [],
                };
            });

        var orchestrator = CreateOrchestrator();
        var chunks = new List<string>();

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1",
            (chunk, _) => { chunks.Add(chunk); return Task.CompletedTask; },
            CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Response.Should().Be("Hello from agent");
        outcome.AssistantMessageId.Should().NotBeEmpty();
        chunks.Should().Equal("Hello ", "from agent");
        // A successful turn folds its usage into the conversation-lifetime budget.
        _budget.Verify(
            b => b.RecordUsageAsync("c1", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_LeaseLostMidTurn_StopsTheTurnAndSaysWhy()
    {
        // A durable lease expires, so a host that stalls long enough can have its lease taken while
        // its turn is still running. Unless that loss is linked into the token driving the turn, this
        // host carries on writing to a transcript another host is now writing to. Reported distinctly
        // from a client disconnect: both arrive as a cancellation, and only one of them is routine.
        var lease = new ControllableTurnLease();
        _turnLease = lease;

        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<object, CancellationToken>((_, ct) =>
            {
                // Another host takes the lease mid-dispatch. The turn's token is the linked one, so
                // it is cancelled here — which is how a real dispatch would observe the loss.
                lease.Steal();
                ct.IsCancellationRequested.Should().BeTrue(
                    "the lease's LeaseLost token must be linked into the token the turn runs under");
                throw new OperationCanceledException(ct);
            });

        var orchestrator = CreateOrchestrator();

        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("This conversation was continued elsewhere*");
        lease.Released.Should().BeTrue("the lease must be released even when the turn ends this way");
    }

    [Fact]
    public async Task SendMessage_ClientDisconnects_StaysACancellationRatherThanALostLease()
    {
        // The control for the test above. A disconnect cancels the caller's own token, and must keep
        // surfacing as a cancellation — a handler that reported "continued elsewhere" for any
        // cancellation would look correct in the stolen case while lying in the ordinary one.
        var lease = new ControllableTurnLease();
        _turnLease = lease;

        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        using var caller = new CancellationTokenSource();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<object, CancellationToken>((_, ct) =>
            {
                caller.Cancel();
                throw new OperationCanceledException(ct);
            });

        var orchestrator = CreateOrchestrator();

        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendMessage_ConversationBudgetExhausted_DeclinesGracefullyWithoutDispatch()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        // Budget already exhausted before this turn.
        _budget
            .Setup(b => b.GetStatusAsync("c1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationBudgetStatus(true, 100, 100));

        var orchestrator = CreateOrchestrator();

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeTrue("a budget decline is graceful, not an error");
        outcome.BudgetExhausted.Should().BeTrue();
        outcome.Response.Should().Contain("token budget");
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never,
            "no LLM turn should be dispatched once the budget is exhausted");
    }

    [Fact]
    public async Task SendMessage_MediatorThrows_ReturnsFailedOutcome()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM failure"));

        var orchestrator = CreateOrchestrator();

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().NotBeNullOrEmpty();
        _healthTracker.Verify(h => h.RecordError("agent"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_MediatorThrows_AppendsSyntheticErrorMessage()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var orchestrator = CreateOrchestrator();

        await orchestrator.SendMessageAsync("conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        _store.Verify(s => s.AppendMessageAsync("c1", "user1",
            It.Is<ConversationMessage>(m => m.Role == MessageRole.Assistant && m.Content.Contains("[Error]")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_AgentReturnsFailure_ReturnsFailedOutcome()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = "",
                UpdatedHistory = [],
                Error = "Content blocked",
            });

        var orchestrator = CreateOrchestrator();

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        _healthTracker.Verify(h => h.RecordError("agent"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ConfigurationError_InDevelopment_SurfacesActionableMessage()
    {
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = "",
                UpdatedHistory = [],
                Error = actionable,
                ErrorKind = AgentTurnErrorKind.Configuration,
            });

        var orchestrator = CreateOrchestrator(environmentName: "Development");

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Be(actionable);
    }

    [Fact]
    public async Task SendMessage_ConfigurationError_InProduction_StaysGeneric()
    {
        const string actionable =
            "Anthropic client is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.";
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = "",
                UpdatedHistory = [],
                Error = actionable,
                ErrorKind = AgentTurnErrorKind.Configuration,
            });

        var orchestrator = CreateOrchestrator(environmentName: "Production");

        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Be("An error occurred processing your request.");
    }

    [Fact]
    public async Task SendMessage_WrongOwner_ThrowsUnauthorizedAccessException()
    {
        _store.Setup(s => s.GetAsync("c1", "attacker", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationAccessDeniedException());

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "attacker", null, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _store.Verify(
            s => s.AppendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a refused turn must not leave the attacker's message in someone else's transcript");
    }

    // ── RetryFromMessage ─────────────────────────────────────────────────

    [Fact]
    public async Task RetryFromMessage_Success_ReturnsOutcomeWithKeepCount()
    {
        var userMsg = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Original", DateTimeOffset.UtcNow);
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [userMsg]);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [userMsg]));
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { userMsg });

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Retried", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.RetryFromMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "user1", null, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.HistoryKeepCount.Should().Be(1);
    }

    [Fact]
    public async Task RetryFromMessage_OnHistoryTruncatedFiresBeforeOnChunk()
    {
        // #328: a client appending streamed deltas onto its still-untruncated local message list
        // before being told to drop the stale tail is a real ordering bug this callback exists to
        // prevent (caught by AgentTelemetryHubStreamingInvariantTests' I3 check). Proves the REAL
        // orchestrator — not just the hub's own wiring — calls onHistoryTruncated before any
        // onChunk delta, and with the correct surviving-message count.
        var userMsg = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Original", DateTimeOffset.UtcNow);
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [userMsg]);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [userMsg]));
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { userMsg });
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var events = new List<string>();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                events.Add("dispatch"); // the model call itself happens strictly after truncation
                return new AgentTurnResult { Success = true, Response = "Retried", UpdatedHistory = [] };
            });

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.RetryFromMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "user1",
            onChunk: (_, _) => { events.Add("chunk"); return Task.CompletedTask; },
            CancellationToken.None,
            onHistoryTruncated: (keepCount, _) => { events.Add($"truncated:{keepCount}"); return Task.CompletedTask; });

        outcome.HistoryKeepCount.Should().Be(1);
        events.Should().StartWith("truncated:1",
            "onHistoryTruncated must fire before the turn (and therefore before any streamed delta) dispatches");
    }

    [Fact]
    public async Task RetryFromMessage_NoUserMessage_ThrowsInvalidOperationException()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.RetryFromMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "user1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retry*");
    }

    // ── EditAndResubmit ──────────────────────────────────────────────────

    [Fact]
    public async Task EditAndResubmit_Success_ReturnsOutcomeWithKeepCount()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Edited", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.EditAndResubmitAsync(
            "conn1", "c1", Guid.NewGuid(), Guid.NewGuid(), "New content", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.HistoryKeepCount.Should().Be(0);
    }

    [Fact]
    public async Task EditAndResubmit_AppendsTheEditedMessageBeforeSignalingTruncation()
    {
        // Code-review finding: unlike RetryFromMessageAsync, this method's onHistoryTruncated is
        // what the client acts on to optimistically re-insert the edited message — so if it fired
        // before the edit was durably appended, an append failure would leave the client showing
        // an edit the server never persisted, with nothing to roll it back. Proves the real
        // orchestrator appends first.
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Edited", UpdatedHistory = [] });

        var events = new List<string>();
        _store.Setup(s => s.AppendMessageAsync("c1", "user1", It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("append"))
            .Returns(Task.CompletedTask);

        var orchestrator = CreateOrchestrator();
        await orchestrator.EditAndResubmitAsync(
            "conn1", "c1", Guid.NewGuid(), Guid.NewGuid(), "New content", "user1",
            onChunk: null, CancellationToken.None,
            onHistoryTruncated: (_, _) => { events.Add("truncated"); return Task.CompletedTask; });

        // DispatchTurnAsync appends the assistant's own response afterward — a second, unrelated
        // "append" this test isn't about — so check relative order of the first two events rather
        // than asserting the full sequence.
        events.Take(2).Should().Equal("append", "truncated");
    }

    [Fact]
    public async Task RetryFromMessage_OnHistoryTruncatedFailure_IsSwallowedAndTheTurnStillDispatches()
    {
        // The truncation this signals already committed durably in the store — a transport failure
        // delivering the notice (dropped connection, slow client) must not abort a turn whose
        // underlying mutation already succeeded.
        var userMsg = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Original", DateTimeOffset.UtcNow);
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [userMsg]);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", "user1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [userMsg]));
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage> { userMsg });
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Retried", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.RetryFromMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "user1",
            onChunk: null, CancellationToken.None,
            onHistoryTruncated: (_, _) => throw new IOException("client connection dropped"));

        outcome.Success.Should().BeTrue("a failed notification must not abort a turn whose truncation already committed");
        outcome.HistoryKeepCount.Should().Be(1);
    }

    // ── ValidateAccess ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAccess_ValidOwner_Succeeds()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        await orchestrator.ValidateAccessAsync("c1", "user1", CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAccess_NotFound_ThrowsInvalidOperationException()
    {
        _store.Setup(s => s.GetAsync("missing", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.ValidateAccessAsync("missing", "user1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ValidateAccess_WrongOwner_ThrowsUnauthorizedAccessException()
    {
        _store.Setup(s => s.GetAsync("c1", "attacker", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationAccessDeniedException());

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.ValidateAccessAsync("c1", "attacker", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── HandleDisconnect ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleDisconnect_TrackedConnection_UntracksAndEndsSession()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 3, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();
        await orchestrator.HandleDisconnectAsync("conn1", null, CancellationToken.None);

        _obsStore.Verify(
            s => s.EndSessionAsync(sessionId, SessionStatus.Completed, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleDisconnect_WithException_RecordsAStatusTheSchemaAccepts()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();
        var ex = new Exception("Connection lost");
        await orchestrator.HandleDisconnectAsync("conn1", ex, CancellationToken.None);

        // This test used to assert the literal "errored" and pass, while production wrote a word the
        // sessions table refuses — the mock accepted it, Postgres did not, the store logged and
        // swallowed the rejection, and every connection that dropped with an exception left its session
        // open forever. A mock enforces nothing; the type does.
        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId, SessionStatus.Error, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleDisconnect_WithCancellation_RecordsCancelledRatherThanError()
    {
        // A client that navigated away, or a host shutting down, arrives here as an
        // OperationCanceledException. It is the single most common way a conversation ends and it is
        // not a failure — but until #301 gave the schema a word for it, every one of them was
        // recorded as an error, in the column the sessions list and the Grafana $status filter both
        // read. (It does not move any error-rate number; no panel computes one from this column.)
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await orchestrator.HandleDisconnectAsync(
            "conn1", new OperationCanceledException(), stopping.Token);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId, SessionStatus.Cancelled, "connection.cancelled", It.IsAny<CancellationToken>()),
            Times.Once);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                It.IsAny<Guid>(), SessionStatus.Error, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The write that closes the session must not carry the token that says the connection is gone,
    /// or the session is never closed at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the defect #301 was filed about, arriving by a second route nobody had looked at.
    /// SignalR aborts the connection <em>before</em> dispatching disconnect — its own source says so:
    /// <c>// Ensure the connection is aborted before firing disconnect</c> — so the token the hub
    /// hands this method is already cancelled every single time. Handing that token to the terminal
    /// <c>EndSessionAsync</c> means the UPDATE is refused before it is ever sent, and
    /// <c>PostgresObservabilityStore</c> catches <em>every</em> exception on a telemetry write and
    /// logs a warning, cancellation included. The row is left <c>status='active'</c> with no
    /// <c>ended_at</c>, for ever, on every ordinary disconnect.
    /// </para>
    /// <para>
    /// The status fix on its own would have been theatre: it would have chosen the right word and
    /// then failed to write it. Cleanup after a cancellation is not itself cancellable, which is the
    /// same rule <c>RunConversationCommandHandler.EndRunSessionAsync</c> already follows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HandleDisconnect_WithCancellation_EndsSessionWithALiveTokenSoTheWriteLands()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();

        // Exactly what the hub passes: a token that is already cancelled.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await orchestrator.HandleDisconnectAsync("conn1", new OperationCanceledException(), aborted.Token);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId,
                SessionStatus.Cancelled,
                It.IsAny<string?>(),
                It.Is<CancellationToken>(t => !t.IsCancellationRequested)),
            Times.Once);
    }

    /// <summary>
    /// The same rule on the error path: a connection that died from a transport fault also arrives
    /// with an aborted token, and its session must still be closed.
    /// </summary>
    [Fact]
    public async Task HandleDisconnect_WithException_EndsSessionWithALiveTokenSoTheWriteLands()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await orchestrator.HandleDisconnectAsync("conn1", new InvalidOperationException("boom"), aborted.Token);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId,
                SessionStatus.Error,
                It.IsAny<string?>(),
                It.Is<CancellationToken>(t => !t.IsCancellationRequested)),
            Times.Once);
    }

    /// <summary>
    /// A transport that timed out is a failure, even though it arrives as the same exception type a
    /// deliberate stop does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TaskCanceledException</c> derives from <c>OperationCanceledException</c> and is what a
    /// keepalive or read timeout throws with nobody having cancelled anything. Matching on the type
    /// alone reclassified a broken transport as a tidy goodbye — and, because the error log had just
    /// been routed through a status check, stopped recording it at any level at all.
    /// </para>
    /// <para>
    /// Read this test for what it is: it pins the classification <em>rule</em>, not a production
    /// guarantee. The only real caller is <c>AgentTelemetryHub.OnDisconnectedAsync</c>, which passes
    /// <c>Context.ConnectionAborted</c> — and SignalR aborts the connection before dispatching
    /// disconnect, so in production that token is always already cancelled and this branch cannot be
    /// reached. Passing <c>CancellationToken.None</c> here exercises a caller shape the hub never
    /// produces. That is worth stating plainly rather than letting the name imply cover that is not
    /// there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HandleDisconnect_WithATimeoutRatherThanACancellation_StillRecordsError()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();

        // Nothing cancelled: the token is live, the exception merely looks like a cancellation.
        await orchestrator.HandleDisconnectAsync(
            "conn1", new TaskCanceledException("read timed out"), CancellationToken.None);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId, SessionStatus.Error, "connection.dropped_with_exception",
                It.IsAny<CancellationToken>()),
            Times.Once);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                It.IsAny<Guid>(), SessionStatus.Cancelled, It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleDisconnect_WithException_RecordsAStableCodeRatherThanTheExceptionText()
    {
        // Fixing the status turned this from a swallowed write into a real one, and that is exactly what
        // makes the reason worth guarding: sessions.error_message is read back out and served on the
        // session list, so an exception's own text would put connection strings, tokens and internal
        // paths in front of a client. It reached nothing before only because Postgres was rejecting the
        // whole statement.
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();
        var secret = new Exception("Host=db;Password=hunter2;SharedAccessSignature=sig");
        await orchestrator.HandleDisconnectAsync("conn1", secret, CancellationToken.None);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                sessionId,
                SessionStatus.Error,
                It.Is<string?>(reason =>
                    reason != null
                    && !reason.Contains("hunter2")
                    && !reason.Contains("SharedAccessSignature")
                    && reason.StartsWith("connection.")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleDisconnect_TrackedConnection_GivesBackTheConnectionItCounted()
    {
        // The hub counts connections, which is the one of the three questions the old shared gauge
        // asked that genuinely belongs to this transport (issue #289). Both halves are asserted: a
        // gauge nobody touches nets to zero too, so the measurement count is what shows the decrement
        // ran rather than that nothing happened.
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 3, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();

        using var probe = new GaugeProbe(OrchestrationConventions.ConnectionsActive);
        await orchestrator.HandleDisconnectAsync("conn1", null, CancellationToken.None);

        probe.Measurements.Should().Be(1);
        probe.Net.Should().Be(-1, "the connection this disconnect ended must stop being counted as live");
    }

    [Fact]
    public async Task SendMessage_CountsTheTurnAsAgentWorkInFlightAndGivesItBack()
    {
        // The hub dispatches a turn straight to ExecuteAgentTurnCommand and never goes through
        // RunConversationCommand, so counting runs only in the bundle and AG-UI paths would leave the
        // "Active Runs" headline reading zero on a SignalR deployment while the agent is generating.
        // Both halves asserted: an untouched gauge nets to zero too.
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _connectionTracker.Setup(t => t.Get("conn1")).Returns((ActiveConversationInfo?)null);
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Hi", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();

        using var probe = new GaugeProbe(OrchestrationConventions.RunsActive);
        await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        probe.Measurements.Should().Be(2, "the turn must be counted up when it starts and down when it ends");
        probe.Net.Should().Be(0, "a finished turn is not work in flight");
    }

    [Fact]
    public async Task SendMessage_SwitchingConversationFails_DoesNotGiveBackAConnectionItStillHolds()
    {
        // The switch decrements the conversation being left and increments the one being joined. If the
        // decrement runs before the work that can fail, a failure leaves the tracker still holding the
        // OLD entry — which the eventual disconnect then decrements a second time. Two decrements for
        // one increment, and an up-down counter never recovers from that: the dashboard reads a
        // negative number of live connections until the process restarts.
        //
        // Provoked through the store because that is how it happens in production: the user navigates
        // away and the token cancels, or the new conversation refuses the caller, while the connection
        // is mid-switch.
        var tracked = new ActiveConversationInfo(
            "c1", "agent", "user1", DateTimeOffset.UtcNow, 2, Guid.NewGuid());
        _connectionTracker.Setup(t => t.Get("conn1")).Returns(tracked);

        var target = new ConversationRecord("c2", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        // Only the sequence is set up. A plain GetAsync setup here would be replaced by it, and a
        // GetHistoryForDispatch setup would never be reached — the switch throws on the second read,
        // which happens before any dispatch. Both were present and both were dead; left in place they
        // suggest this test exercises a dispatch it never gets near.
        //
        // The recorder reads the record again to decide whether to adopt a session. That read is the
        // await sitting between the two gauge movements.
        _store.SetupSequence(s => s.GetAsync("c2", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(target)
            .ThrowsAsync(new ConversationAccessDeniedException());

        var orchestrator = CreateOrchestrator();

        using var probe = new GaugeProbe(OrchestrationConventions.ConnectionsActive);
        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c2", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        probe.Net.Should().Be(0,
            "a switch that failed released nothing, so it must not report having released anything — "
            + "the connection is still tracked on the conversation it was already on");
    }

    [Fact]
    public async Task HandleDisconnect_UntrackedConnection_NoOp()
    {
        _connectionTracker.Setup(t => t.Untrack("unknown")).Returns((ActiveConversationInfo?)null);

        var orchestrator = CreateOrchestrator();
        await orchestrator.HandleDisconnectAsync("unknown", null, CancellationToken.None);

        _obsStore.Verify(
            s => s.EndSessionAsync(
                It.IsAny<Guid>(), It.IsAny<SessionStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Session tracking ─────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_FirstTurn_StartsObservabilitySession()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        var sessionId = Guid.NewGuid();
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessionId);
        _connectionTracker.Setup(t => t.Get("conn1")).Returns((ActiveConversationInfo?)null);

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Hi", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();
        await orchestrator.SendMessageAsync("conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        _obsStore.Verify(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()), Times.Once);
        _connectionTracker.Verify(t => t.Track("conn1", It.Is<ActiveConversationInfo>(i => i.ConversationId == "c1")), Times.AtLeastOnce);
    }

    /// <summary>
    /// A conversation that already has a session and totals must be continued, not restarted.
    /// </summary>
    /// <remarks>
    /// The defect this replaces (issue #280): the hub opened a session on every conversation switch and
    /// accumulated totals on a per-<em>connection</em> object that starts at zero. Because the
    /// observability row is keyed one-per-conversation and written with SET semantics, reconnecting
    /// restamped the session's start time and then overwrote the conversation's whole rollup with what
    /// the new connection had spent. Nothing errored; the dashboard just showed a long conversation as
    /// a short one.
    /// </remarks>
    [Fact]
    public async Task SendMessage_ReconnectingToAConversationWithHistory_ContinuesItsTotals()
    {
        var existingSession = Guid.NewGuid();
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])
        {
            ObservabilitySessionId = existingSession,
            Telemetry = new TelemetryAccumulator(
                TurnCount: 7, ToolCallCount: 3, InputTokens: 1_000, OutputTokens: 500,
                CacheRead: 200, CacheWrite: 100, CostUsd: 1.25m),
        };
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        // A brand-new connection, exactly as after a reconnect: it knows nothing about the conversation.
        _connectionTracker.Setup(t => t.Get("conn-fresh")).Returns((ActiveConversationInfo?)null);

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true, Response = "Hi", UpdatedHistory = [],
                InputTokens = 10, OutputTokens = 20, CostUsd = 0.5m,
            });

        var orchestrator = CreateOrchestrator();
        await orchestrator.SendMessageAsync(
            "conn-fresh", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        _obsStore.Verify(
            s => s.StartSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the conversation already has a session; opening a second restamps its start time and every "
            + "duration derived from it");

        _obsStore.Verify(
            s => s.UpdateSessionMetricsAsync(
                existingSession,
                8,              // turn count: continued from 7, not restarted at 1
                3,              // tool calls carried forward
                0,
                1_010,          // input tokens: 1,000 + this turn's 10
                520,
                200,
                100,
                1.75m,
                It.IsAny<decimal>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the row is written with SET semantics, so anything but the conversation's cumulative total "
            + "silently replaces its history with one connection's share of it");
    }

    /// <summary>
    /// The turn number a reconnected client sees continues the conversation's sequence.
    /// </summary>
    /// <remarks>
    /// It used to come from the message count, which advances by two per turn — so the same conversation
    /// produced one sequence over the hub and a different one over the bundle path, in one key space.
    /// </remarks>
    [Fact]
    public async Task SendMessage_ConversationWithHistory_NumbersTheTurnFromTheConversationsTurnCount()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])
        {
            ObservabilitySessionId = Guid.NewGuid(),
            Telemetry = TelemetryAccumulator.Zero with { TurnCount = 4 },
        };
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _connectionTracker.Setup(t => t.Get("conn1")).Returns((ActiveConversationInfo?)null);

        ExecuteAgentTurnCommand? dispatched = null;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((c, _) => dispatched = (ExecuteAgentTurnCommand)c)
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "Hi", UpdatedHistory = [] });

        var orchestrator = CreateOrchestrator();
        await orchestrator.SendMessageAsync("conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        dispatched!.TurnNumber.Should().Be(5);
    }

    // ── Streaming ────────────────────────────────────────────────────────

    // ── Disconnect vs timeout ────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_ClientDisconnectMidTurn_AbortsWithoutRecordingError()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        // Simulate a client disconnect during the turn: the connection token cancels and
        // the handler surfaces a failed result tagged Cancelled.
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cts.CancelAsync();
                return new AgentTurnResult
                {
                    Success = false, Response = "", UpdatedHistory = [],
                    Error = "cancelled", ErrorKind = AgentTurnErrorKind.Cancelled,
                };
            });

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, cts.Token);

        // A disconnect is routine cancellation — abort, don't classify as an agent error.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _healthTracker.Verify(h => h.RecordError(It.IsAny<string>()), Times.Never);
        _store.Verify(s => s.AppendMessageAsync("c1", "user1",
            It.Is<ConversationMessage>(m => m.Content.Contains("[Error]")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_GenuineFailureCoincidingWithDisconnect_StillRecordsError()
    {
        // The tightening: discrimination is by ErrorKind, not raw token state. A genuine
        // agent failure (ErrorKind.Internal) that happens to coincide with the connection
        // dropping must still be recorded — not silently reclassified as a disconnect.
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cts.CancelAsync(); // client drops at the same instant
                return new AgentTurnResult
                {
                    Success = false, Response = "", UpdatedHistory = [],
                    Error = "provider error", ErrorKind = AgentTurnErrorKind.Internal,
                };
            });

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, cts.Token);

        outcome.Success.Should().BeFalse();
        _healthTracker.Verify(h => h.RecordError("agent"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ClientDisconnect_OceFromDispatch_RethrowsWithoutRecordingError()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _healthTracker.Verify(h => h.RecordError(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_Timeout_StillRecordsErrorAndReturnsFailedOutcome()
    {
        // A timeout cancels a linked token (TimeoutException), leaving the connection
        // token uncancelled — so it must still be treated as a genuine agent error,
        // unlike a client disconnect.
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request exceeded timeout."));

        var orchestrator = CreateOrchestrator();
        var outcome = await orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        _healthTracker.Verify(h => h.RecordError("agent"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ForwardsHandlerDeltasVerbatim_WithoutRechunking()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        // A single long delta from the handler must reach the client as one chunk — the
        // old 50-char re-chunker is gone; the orchestrator no longer reshapes the stream.
        var longDelta = new string('x', 120);
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var sink = AgentTurnStreamSink.Current;
                if (sink is not null)
                    await sink.EmitAsync(longDelta, CancellationToken.None);
                return new AgentTurnResult { Success = true, Response = longDelta, UpdatedHistory = [] };
            });

        var chunks = new List<string>();
        var orchestrator = CreateOrchestrator();

        await orchestrator.SendMessageAsync("conn1", "c1", Guid.NewGuid(), "Hello", "user1",
            (chunk, _) => { chunks.Add(chunk); return Task.CompletedTask; },
            CancellationToken.None);

        chunks.Should().ContainSingle("the orchestrator forwards handler deltas without re-chunking");
        chunks[0].Should().Be(longDelta);
    }
}
