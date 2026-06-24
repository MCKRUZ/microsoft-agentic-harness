using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Budget;
using FluentAssertions;
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
using Xunit;

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
    private readonly ConversationLockRegistry _lockRegistry = new();
    private readonly AgentHubConfig _config = new() { MaxHistoryMessages = 20 };

    public ConversationOrchestratorTests()
    {
        // Budget disabled by default — most tests don't exercise the conversation budget.
        _budget.Setup(b => b.GetStatus(It.IsAny<string>())).Returns(ConversationBudgetStatus.Disabled);
    }

    private ConversationOrchestrator CreateOrchestrator(string environmentName = "Development")
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return new(
            _mediator.Object,
            _store.Object,
            _lockRegistry,
            _healthTracker.Object,
            _obsStore.Object,
            _connectionTracker.Object,
            _budget.Object,
            Options.Create(_config),
            environment.Object,
            NullLogger<ConversationOrchestrator>.Instance);
    }

    // ── StartConversation ────────────────────────────────────────────────

    [Fact]
    public async Task StartConversation_NewConversation_CreatesRecord()
    {
        var expected = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);
        _store.Setup(s => s.CreateAsync("agent", "user1", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        var orchestrator = CreateOrchestrator();
        var (record, history) = await orchestrator.StartConversationAsync("conn1", "agent", null, "user1", CancellationToken.None);

        record.Should().Be(expected);
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task StartConversation_ExistingConversation_ReturnsExistingRecord()
    {
        var existing = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        var orchestrator = CreateOrchestrator();
        var (record, _) = await orchestrator.StartConversationAsync("conn1", "agent", "c1", "user1", CancellationToken.None);

        record.Should().Be(existing);
        _store.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartConversation_WrongOwner_ThrowsUnauthorizedAccessException()
    {
        var other = new ConversationRecord("c1", "agent", "other-user", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(other);

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.StartConversationAsync("conn1", "agent", "c1", "attacker", CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── SetSettings ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetSettings_ValidOwner_UpdatesSettings()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.UpdateSettingsAsync("c1", It.IsAny<ConversationSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        var settings = new ConversationSettings("gpt-4o", 0.7f, null);

        await orchestrator.SetSettingsAsync("c1", settings, "user1", CancellationToken.None);

        _store.Verify(s => s.UpdateSettingsAsync("c1", settings, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetSettings_NotFound_ThrowsInvalidOperationException()
    {
        _store.Setup(s => s.GetAsync("missing", It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _budget.Verify(b => b.RecordUsage("c1", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ConversationBudgetExhausted_DeclinesGracefullyWithoutDispatch()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        // Budget already exhausted before this turn.
        _budget.Setup(b => b.GetStatus("c1")).Returns(new ConversationBudgetStatus(true, 100, 100));

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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());

        _obsStore.Setup(s => s.StartSessionAsync("c1", "agent", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var orchestrator = CreateOrchestrator();

        await orchestrator.SendMessageAsync("conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        _store.Verify(s => s.AppendMessageAsync("c1",
            It.Is<ConversationMessage>(m => m.Role == MessageRole.Assistant && m.Content.Contains("[Error]")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_AgentReturnsFailure_ReturnsFailedOutcome()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        var record = new ConversationRecord("c1", "agent", "other-user", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "attacker", null, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── RetryFromMessage ─────────────────────────────────────────────────

    [Fact]
    public async Task RetryFromMessage_Success_ReturnsOutcomeWithKeepCount()
    {
        var userMsg = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Original", DateTimeOffset.UtcNow);
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [userMsg]);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [userMsg]));
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
    public async Task RetryFromMessage_NoUserMessage_ThrowsInvalidOperationException()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.TruncateFromMessageAsync("c1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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

    // ── ValidateAccess ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAccess_ValidOwner_Succeeds()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        await orchestrator.ValidateAccessAsync("c1", "user1", CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAccess_NotFound_ThrowsInvalidOperationException()
    {
        _store.Setup(s => s.GetAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        var orchestrator = CreateOrchestrator();
        var act = () => orchestrator.ValidateAccessAsync("missing", "user1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ValidateAccess_WrongOwner_ThrowsUnauthorizedAccessException()
    {
        var record = new ConversationRecord("c1", "agent", "other", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);

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

        _obsStore.Verify(s => s.EndSessionAsync(sessionId, "completed", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleDisconnect_WithException_RecordsErroredStatus()
    {
        var sessionId = Guid.NewGuid();
        var info = new ActiveConversationInfo("c1", "agent", "user1", DateTimeOffset.UtcNow, 1, sessionId);
        _connectionTracker.Setup(t => t.Untrack("conn1")).Returns(info);

        var orchestrator = CreateOrchestrator();
        var ex = new Exception("Connection lost");
        await orchestrator.HandleDisconnectAsync("conn1", ex, CancellationToken.None);

        _obsStore.Verify(s => s.EndSessionAsync(sessionId, "errored", "Connection lost", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleDisconnect_UntrackedConnection_NoOp()
    {
        _connectionTracker.Setup(t => t.Untrack("unknown")).Returns((ActiveConversationInfo?)null);

        var orchestrator = CreateOrchestrator();
        await orchestrator.HandleDisconnectAsync("unknown", null, CancellationToken.None);

        _obsStore.Verify(s => s.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Session tracking ─────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_FirstTurn_StartsObservabilitySession()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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

    // ── Streaming ────────────────────────────────────────────────────────

    // ── Disconnect vs timeout ────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_ClientDisconnectMidTurn_AbortsWithoutRecordingError()
    {
        var record = new ConversationRecord("c1", "agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Verify(s => s.AppendMessageAsync("c1",
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
        _store.Setup(s => s.GetAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.GetHistoryForDispatch("c1", 20, It.IsAny<CancellationToken>()))
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
