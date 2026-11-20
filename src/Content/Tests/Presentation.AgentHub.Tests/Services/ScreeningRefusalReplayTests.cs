using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using Application.AI.Common.Services.AI;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Budget;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// A turn that was never admitted leaves nothing in the conversation (#959, #964). The next turn reads its history back
/// from the store unscreened, so a message stored before screening let it pass reaches the model as soon as the caller
/// sends anything that does. The message is stored only when the turn handler admits the turn, so every exit before
/// that (a refusal, a validation rejection, a failure, a disconnect, a timeout, a spent budget) stores nothing.
/// </summary>
/// <remarks>
/// <para>
/// Against a real store, because the defect was what the store kept: a mocked store would only show what the
/// orchestrator asked for, not what the next turn's history holds.
/// </para>
/// <para>
/// The mediator is mocked, so a mock that lets the turn through calls <see cref="AgentTurnAdmission.AdmitCurrentAsync"/>
/// itself, standing in for the handler. A mock that throws without calling it is a turn the pipeline stopped before the
/// handler.
/// </para>
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
        _store = TestConversationStore.Create(_dir);
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

    private ConversationOrchestrator Orchestrator(bool budgetSpent = false)
    {
        var budget = new Mock<IConversationBudgetTracker>();
        budget.Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(budgetSpent ? new ConversationBudgetStatus(true, 100, 100) : ConversationBudgetStatus.Disabled);
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

    [Theory]
    [MemberData(nameof(RejectionCases.All), MemberType = typeof(RejectionCases))]
    public async Task ARefusedTurn_IsNotInTheNextTurnsHistory(Exception refusal, string toldTheCaller)
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
                return AdmitThen(Answer("ok"));
            });
        var orchestrator = Orchestrator();

        var refusedMessageId = Guid.NewGuid();
        var first = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, refusedMessageId, Refused, "user-a", null, CancellationToken.None);
        var second = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "continue", "user-a", null, CancellationToken.None);

        first.Success.Should().BeFalse();
        first.ErrorMessage.Should().Be(toldTheCaller, "the caller is told why, without the text");
        first.RemovedMessageId.Should().Be(refusedMessageId, "the client is told which message to drop (#1036)");
        second.Success.Should().BeTrue();
        secondTurnHistory.Should().NotBeNull();
        secondTurnHistory!.Select(m => m.Text).Should().NotContain(text => text.Contains(Refused),
            "a refused message replayed as history reaches the model unscreened");
        (await _store.GetAsync(conversation.Id, "user-a"))!.Messages.Select(m => m.Content)
            .Should().NotContain(content => content.Contains(Refused), "the store keeps nothing of the refused turn");
        _health.Verify(h => h.RecordError(It.IsAny<string>()), Times.Never, "a refusal is not the agent failing");
    }

    [Theory]
    [MemberData(nameof(RejectionCases.All), MemberType = typeof(RejectionCases))]
    public async Task ARefusedEdit_LeavesTheConversationAsItWas(Exception refusal, string toldTheCaller)
    {
        // #962: the edit used to cut the original before screening, so a refused edit lost both, with nothing to retry.
        var conversation = await SeedExchangeAsync("the original question");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        var truncations = new List<int>();

        var editedMessageId = Guid.NewGuid();
        var outcome = await Orchestrator().EditAndResubmitAsync(
            "conn", conversation.Id, conversation.UserMessageId, editedMessageId, Refused, "user-a", null,
            CancellationToken.None, RecordTruncation(truncations));

        outcome.ErrorMessage.Should().Be(toldTheCaller);
        outcome.RemovedMessageId.Should().Be(editedMessageId, "the client stages the edit and must not keep it");
        outcome.HistoryKeepCount.Should().BeNull("nothing was truncated");
        truncations.Should().BeEmpty("the client keeps showing the original only if it is not told to cut it");
        (await ContentsAsync(conversation.Id)).Should().Equal("the original question", "an answer");
        _health.Verify(h => h.RecordError(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ARetryDuringAScreeningOutage_KeepsTheMessageAnEarlierTurnStored()
    {
        const string earlier = "the question an earlier turn stored";
        var conversation = await SeedExchangeAsync(earlier);
        var outage = new ContentSafetyUnavailableException();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(outage);

        var outcome = await Orchestrator().RetryFromMessageAsync(
            "conn", conversation.Id, conversation.AssistantMessageId, "user-a", null, CancellationToken.None);

        outcome.ErrorMessage.Should().Be(outage.ClientMessage, "the caller is told to try again");
        outcome.RemovedMessageId.Should().BeNull("the retried message stays, so the client keeps showing it");
        outcome.HistoryKeepCount.Should().BeNull("the old answer is cut only once the retry is admitted");
        (await ContentsAsync(conversation.Id)).Should().Equal([earlier, "an answer"],
            "an outage is no verdict on the text, and the retry never ran, so the answer it would replace stays");
    }

    [Theory]
    [MemberData(nameof(RejectionCases.Verdicts), MemberType = typeof(RejectionCases))]
    public async Task ARetryThatIsDefinitelyRejected_RemovesTheMessage(Exception rejection, string toldTheCaller)
    {
        // The earlier turn may have stored the message without screening it (an outage with BlockWhenUnavailable off,
        // or detection since tightened). Rejected now, it would otherwise be replayed unscreened on the next send.
        var conversation = await SeedExchangeAsync(Refused);
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(rejection);

        var truncations = new List<int>();

        var outcome = await Orchestrator().RetryFromMessageAsync(
            "conn", conversation.Id, conversation.AssistantMessageId, "user-a", null, CancellationToken.None,
            RecordTruncation(truncations));

        outcome.ErrorMessage.Should().Be(toldTheCaller);
        outcome.RemovedMessageId.Should().Be(conversation.UserMessageId, "the client still shows the retried message");
        outcome.HistoryKeepCount.Should().Be(0, "the message and the answer after it are gone (#1036)");
        truncations.Should().Equal([0], "the client still shows both, and drops them on this notice");
        (await _store.GetAsync(conversation.Id, "user-a"))!.Messages.Select(m => m.Content)
            .Should().NotContain(content => content.Contains(Refused), "a rejected message stays out of the history");
    }

    [Fact]
    public async Task AnAdmittedSend_StoresTheMessageThenTheAnswer()
    {
        var conversation = await _store.CreateAsync("agent", "user-a");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => AdmitThen(Answer("the answer")));

        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "a question", "user-a", null, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.RemovedMessageId.Should().BeNull();
        (await ContentsAsync(conversation.Id)).Should().Equal("a question", "the answer");
    }

    public static TheoryData<Exception> FailuresBeforeAdmission => new()
    {
        // An identity-resolution or authorization failure, a DI failure: thrown before screening even ran.
        new InvalidOperationException("identity could not be resolved"),
        // Screening outlived the turn deadline; it used to bypass BlockWhenUnavailable for slow outages.
        new TimeoutException("the request exceeded its timeout"),
    };

    [Theory]
    [MemberData(nameof(FailuresBeforeAdmission))]
    public async Task AFailureBeforeAdmission_StoresNothing(Exception failure)
    {
        var conversation = await _store.CreateAsync("agent", "user-a");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var messageId = Guid.NewGuid();
        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, messageId, Refused, "user-a", null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.RemovedMessageId.Should().Be(messageId, "the client drops the message the conversation does not hold");
        (await ContentsAsync(conversation.Id)).Should().BeEmpty("neither the unscreened message nor an orphaned error row");
    }

    [Fact]
    public async Task ADisconnectDuringScreening_StoresNothing()
    {
        // The transports used to rethrow the cancellation without removing the stored message, and a caller could time
        // a disconnect to keep it.
        var conversation = await _store.CreateAsync("agent", "user-a");
        using var connection = new CancellationTokenSource();
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((_, ct) =>
            {
                connection.Cancel();
                return Task.FromException<AgentTurnResult>(new OperationCanceledException(ct));
            });

        var send = () => Orchestrator().SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), Refused, "user-a", null, connection.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();
        (await ContentsAsync(conversation.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ScreeningThatFinishesAfterTheTurnGaveUp_CannotStoreTheMessage()
    {
        // TimeoutBehavior abandons the inner pipeline when the deadline passes; screening that then passes would still
        // reach the handler and store the message after the client was told it was dropped.
        var conversation = await _store.CreateAsync("agent", "user-a");
        var screeningDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? abandoned = null;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                abandoned = Task.Run(async () =>
                {
                    await screeningDone.Task;
                    await AgentTurnAdmission.AdmitCurrentAsync(CancellationToken.None);
                });
                return Task.FromException<AgentTurnResult>(new TimeoutException());
            });

        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), Refused, "user-a", null, CancellationToken.None);
        screeningDone.SetResult();
        var late = () => abandoned!;

        outcome.RemovedMessageId.Should().NotBeNull();
        await late.Should().ThrowAsync<OperationCanceledException>("the abandoned turn stops before the model");
        (await ContentsAsync(conversation.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ATurnDeclinedForASpentBudget_StoresNothing()
    {
        // A caller could keep sending after spending the budget; every message was stored unscreened and replayed as
        // history once the budget came back (a restart on the file store, or an operator raising the ceiling).
        var conversation = await _store.CreateAsync("agent", "user-a");

        var messageId = Guid.NewGuid();
        var outcome = await Orchestrator(budgetSpent: true).SendMessageAsync(
            "conn", conversation.Id, messageId, Refused, "user-a", null, CancellationToken.None);

        outcome.BudgetExhausted.Should().BeTrue();
        outcome.RemovedMessageId.Should().Be(messageId);
        (await ContentsAsync(conversation.Id)).Should().BeEmpty("neither the message nor the notice is stored");
        _mediator.Verify(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailedResultBeforeAdmission_StoresNothing()
    {
        // A pre-turn hook that blocks returns a failed result without the handler running.
        var conversation = await _store.CreateAsync("agent", "user-a");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = false, Response = "", Error = "blocked by a hook", UpdatedHistory = [] });

        var messageId = Guid.NewGuid();
        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, messageId, Refused, "user-a", null, CancellationToken.None);

        outcome.RemovedMessageId.Should().Be(messageId);
        (await ContentsAsync(conversation.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailureAfterAdmission_KeepsTheMessageAndAddsAnErrorRow()
    {
        var conversation = await _store.CreateAsync("agent", "user-a");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => AdmitThen(Task.FromException<AgentTurnResult>(new InvalidOperationException("the model failed"))));

        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "a screened question", "user-a", null, CancellationToken.None);

        outcome.RemovedMessageId.Should().BeNull("the message was admitted and stays");
        (await ContentsAsync(conversation.Id)).Should().Equal("a screened question", "[Error] The agent encountered an error.");
    }

    [Fact]
    public async Task ABlockRaisedAfterAdmission_KeepsTheMessage()
    {
        // ContentSafetyException is documented for output and tool blocks too. Keyed on the exception type, a block
        // raised after the model answered would remove a message that was screened and answered.
        var conversation = await _store.CreateAsync("agent", "user-a");
        var block = new ContentSafetyException("Response contained harmful content.", "violence");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => AdmitThen(Task.FromException<AgentTurnResult>(block)));

        var outcome = await Orchestrator().SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "a screened question", "user-a", null, CancellationToken.None);

        outcome.ErrorMessage.Should().Be(block.ClientMessage);
        outcome.RemovedMessageId.Should().BeNull();
        (await ContentsAsync(conversation.Id)).Should().Equal("a screened question");
    }

    [Fact]
    public async Task AnAdmittedEdit_ReplacesTheOriginalAndIsDispatchedWithoutIt()
    {
        var conversation = await SeedExchangeAsync("the original question");
        var truncations = new List<int>();
        IReadOnlyList<ChatMessage>? history = null;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((command, _) =>
            {
                history = command.ConversationHistory;
                return AdmitThen(Answer("a new answer"), () => truncations.Should().Equal([0], "told on admission, before the deltas"));
            });

        var outcome = await Orchestrator().EditAndResubmitAsync(
            "conn", conversation.Id, conversation.UserMessageId, Guid.NewGuid(), "the edited question", "user-a", null,
            CancellationToken.None, RecordTruncation(truncations));

        outcome.HistoryKeepCount.Should().Be(0);
        history.Should().BeEmpty("the original and its answer are what the edit replaces");
        (await ContentsAsync(conversation.Id)).Should().Equal("the edited question", "a new answer");
    }

    [Fact]
    public async Task AnAdmittedRetry_ReplacesTheOldAnswer()
    {
        var conversation = await SeedExchangeAsync("the question");
        var truncations = new List<int>();
        IReadOnlyList<ChatMessage>? history = null;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((command, _) =>
            {
                history = command.ConversationHistory;
                return AdmitThen(Answer("a better answer"));
            });

        var outcome = await Orchestrator().RetryFromMessageAsync(
            "conn", conversation.Id, conversation.AssistantMessageId, "user-a", null, CancellationToken.None,
            RecordTruncation(truncations));

        outcome.HistoryKeepCount.Should().Be(1);
        truncations.Should().Equal(1);
        history.Should().BeEmpty("the retried question is sent as the message, not as history");
        (await ContentsAsync(conversation.Id)).Should().Equal("the question", "a better answer");
    }

    private static AgentTurnResult Answer(string response) =>
        new() { Success = true, Response = response, UpdatedHistory = [] };

    // Stands in for the turn handler letting the turn through: admission first, then the outcome.
    private static async Task<AgentTurnResult> AdmitThen(Task<AgentTurnResult> outcome, Action? afterAdmission = null)
    {
        await AgentTurnAdmission.AdmitCurrentAsync(CancellationToken.None);
        afterAdmission?.Invoke();
        return await outcome;
    }

    private static Task<AgentTurnResult> AdmitThen(AgentTurnResult result, Action? afterAdmission = null) =>
        AdmitThen(Task.FromResult(result), afterAdmission);

    private static Func<int, CancellationToken, Task> RecordTruncation(List<int> truncations) =>
        (keep, _) => { truncations.Add(keep); return Task.CompletedTask; };

    private async Task<IReadOnlyList<string>> ContentsAsync(string conversationId) =>
        (await _store.GetAsync(conversationId, "user-a"))!.Messages.Select(m => m.Content).ToList();

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
