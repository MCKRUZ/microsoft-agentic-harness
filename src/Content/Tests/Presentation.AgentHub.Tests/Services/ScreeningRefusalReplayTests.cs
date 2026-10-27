using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using Application.AI.Common.Services.AI;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Budget;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// A turn request screening refuses is not replayed to the model on the next turn (#959). The user's message is stored
/// before the turn runs and the next turn reads its history back from the store unscreened, so a refusal left in place
/// would reach the model as soon as the caller sent anything that passes.
/// </summary>
/// <remarks>
/// Against a real store, because the defect was what the store kept: a mocked store would only show that the
/// orchestrator asked for a removal, not that the next turn's history no longer holds the message.
/// </remarks>
public sealed class ScreeningRefusalReplayTests : IDisposable
{
    private const string Refused = "text the screen refused";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "refusal-replay-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ISessionHealthTracker> _health = new();
    private readonly Mock<IObservabilityStore> _observability = new();
    private readonly FileSystemConversationStore _store;

    /// <summary>Creates an isolated transcript directory and the store over it.</summary>
    public ScreeningRefusalReplayTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new FileSystemConversationStore(
            Options.Create(new ConversationsConfig { ConversationsPath = _dir }),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<FileSystemConversationStore>.Instance);
        _observability.Setup(o => o.StartSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        _observability.Setup(o => o.ResumeSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ConversationOrchestrator Orchestrator()
    {
        var budget = new Mock<IConversationBudgetTracker>();
        budget.Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);
        var replay = new Mock<IToolCallReplayTreatment>();
        replay.Setup(t => t.Enabled).Returns(true);
        replay.Setup(t => t.MaxCallsPerTurn).Returns(32);
        replay.Setup(t => t.MaxReplayedChars).Returns(65536);
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        return new ConversationOrchestrator(
            _mediator.Object,
            _store,
            new InProcessConversationTurnLease(),
            new Mock<IAgentConversationCache>().Object,
            _health.Object,
            _observability.Object,
            new ConversationTelemetryRecorder(_observability.Object, _store, NullLogger<ConversationTelemetryRecorder>.Instance),
            new Mock<IConnectionTracker>().Object,
            budget.Object,
            replay.Object,
            Options.Create(new AgentHubConfig { MaxHistoryMessages = 20 }),
            environment.Object,
            NullLogger<ConversationOrchestrator>.Instance);
    }

    public static TheoryData<Exception> Refusals => new()
    {
        new ContentSafetyException("Content blocked by safety policy (hate).", "hate"),
        new ContentSafetyUnavailableException(),
        new PromptInjectionBlockedException("Prompt injection detected (role_play). Request blocked."),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedTurn_IsNotInTheNextTurnsHistory(Exception refusal)
    {
        var conversation = await _store.CreateAsync("agent", "user-a");
        IReadOnlyList<ChatMessage>? secondTurnHistory = null;
        var calls = 0;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((command, _) =>
            {
                if (calls++ == 0)
                    return Task.FromException<AgentTurnResult>(refusal);
                secondTurnHistory = command.ConversationHistory;
                return Task.FromResult(new AgentTurnResult { Success = true, Response = "ok", UpdatedHistory = [] });
            });
        var orchestrator = Orchestrator();

        var first = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), Refused, "user-a", null, CancellationToken.None);
        var second = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "continue", "user-a", null, CancellationToken.None);

        first.Success.Should().BeFalse();
        first.ErrorMessage.Should().Be(((IScreeningRefusal)refusal).ClientMessage, "the caller is told why, without the text");
        second.Success.Should().BeTrue();
        secondTurnHistory.Should().NotBeNull();
        secondTurnHistory!.Select(m => m.Text).Should().NotContain(text => text.Contains(Refused),
            "a refused message replayed as history reaches the model unscreened");
        (await _store.GetAsync(conversation.Id, "user-a"))!.Messages.Select(m => m.Content)
            .Should().NotContain(content => content.Contains(Refused), "the store keeps nothing of the refused turn");
        _health.Verify(h => h.RecordError(It.IsAny<string>()), Times.Never, "a refusal is not the agent failing");
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedEdit_RemovesTheEditedMessageAndMatchesTheCountTheClientIsSent(Exception refusal)
    {
        var conversation = await SeedExchangeAsync("the original question");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);

        var outcome = await Orchestrator().EditAndResubmitAsync(
            "conn", conversation.Id, conversation.UserMessageId, Guid.NewGuid(), Refused, "user-a", null, CancellationToken.None);

        var stored = (await _store.GetAsync(conversation.Id, "user-a"))!.Messages;
        outcome.ErrorMessage.Should().Be(((IScreeningRefusal)refusal).ClientMessage);
        stored.Select(m => m.Content).Should().NotContain(content => content.Contains(Refused),
            "the edited text was stored for this turn and refused");
        outcome.HistoryKeepCount.Should().Be(stored.Count, "the client trims its history to the count it is sent");
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedRetry_KeepsTheMessageAnEarlierTurnAccepted(Exception refusal)
    {
        const string accepted = "the question an earlier turn accepted";
        var conversation = await SeedExchangeAsync(accepted);
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);

        var outcome = await Orchestrator().RetryFromMessageAsync(
            "conn", conversation.Id, conversation.AssistantMessageId, "user-a", null, CancellationToken.None);

        var stored = (await _store.GetAsync(conversation.Id, "user-a"))!.Messages;
        outcome.ErrorMessage.Should().Be(((IScreeningRefusal)refusal).ClientMessage);
        stored.Select(m => m.Content).Should().Contain(accepted,
            "a retry stores nothing new, so a refusal such as a screening outage must not delete the caller's history");
        outcome.HistoryKeepCount.Should().Be(stored.Count, "the client trims its history to the count it is sent");
    }

    private async Task<SeededExchange> SeedExchangeAsync(string question)
    {
        var conversation = await _store.CreateAsync("agent", "user-a");
        var user = new ConversationMessage(Guid.NewGuid(), MessageRole.User, question, DateTimeOffset.UtcNow);
        var assistant = new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "an answer", DateTimeOffset.UtcNow);
        await _store.AppendMessageAsync(conversation.Id, "user-a", user);
        await _store.AppendMessageAsync(conversation.Id, "user-a", assistant);
        return new SeededExchange(conversation.Id, user.Id, assistant.Id);
    }

    private sealed record SeededExchange(string Id, Guid UserMessageId, Guid AssistantMessageId);
}
