using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.AI.Common.Services.AI;
using Domain.AI.Budget;
using Domain.AI.Observability.Models;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Regression tests for the solution review finding "ActiveSessions gauge leaks
/// and observability session is never ended when the conversation throws or is
/// cancelled". Before the fix, an exception or cancellation escaping the turn
/// loop bypassed both the gauge decrement and <c>EndSessionAsync</c>, leaving the
/// session row dangling forever. The fix moves cleanup into catch/finally so the
/// session is always ended and the gauge is always decremented.
/// </summary>
public class RunConversationCommandHandlerSolutionReviewFixTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IObservabilityStore> _observabilityStore = new();
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly RunConversationCommandHandler _handler;

    private static readonly Guid SessionId = Guid.NewGuid();

    public RunConversationCommandHandlerSolutionReviewFixTests()
    {
        _observabilityStore
            .Setup(s => s.StartSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionId);

        var budget = new Mock<IConversationBudgetTracker>();
        budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        // Strict and unstubbed: these tests run self-contained conversations, so any call to the
        // transcript store or the turn lease means the handler took the durable path by mistake.
        var strictStore = new Mock<IConversationStore>(MockBehavior.Strict).Object;

        _handler = new RunConversationCommandHandler(
            _mediator.Object,
            _agentCache.Object,
            budget.Object,
            _observabilityStore.Object,
            // The strict store is shared with the recorder deliberately: a self-contained run must not
            // touch it, and the recorder is now the thing that would.
            new ConversationTelemetryRecorder(
                _observabilityStore.Object, strictStore, NullLogger<ConversationTelemetryRecorder>.Instance),
            strictStore,
            new Mock<IConversationTurnLease>(MockBehavior.Strict).Object,
            Options.Create(new ConversationsConfig()),
            NullLogger<RunConversationCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_TurnThrowsUnhandledException_EndsSessionWithErrorStatus()
    {
        // Arrange — the turn pipeline throws (e.g. an escaping infrastructure exception).
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // Act
        var act = () => _handler.Handle(command, CancellationToken.None);

        // Assert — exception propagates, but the session is ended (not left dangling).
        await act.Should().ThrowAsync<InvalidOperationException>();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(SessionId, SessionStatus.Error, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_TurnThrowsException_DoesNotLeakRawMessageIntoSessionReason()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret connection string leaked here"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // Act
        var act = () => _handler.Handle(command, CancellationToken.None);

        // Assert — the persisted reason is a stable scrubbed code, never the raw message.
        await act.Should().ThrowAsync<InvalidOperationException>();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                SessionId,
                SessionStatus.Error,
                It.Is<string?>(r => r != null && !r.Contains("secret") && r.StartsWith("conversation.")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A model call that times out is a failure, not a cancellation, even though it arrives as the
    /// same exception type.
    /// </summary>
    /// <remarks>
    /// <c>TaskCanceledException</c> derives from <c>OperationCanceledException</c> and is what an
    /// HTTP client throws when a request exceeds its timeout — with nobody having cancelled anything.
    /// While the cancellation arm caught the base type unfiltered, a timed-out turn closed as
    /// <c>Cancelled</c>: absent from the error rate, and silent, because only the general handler
    /// logs. That traded an over-reported failure for an unreported one. The token, not the exception
    /// type, is what says whether a stop was asked for.
    /// </remarks>
    [Fact]
    public async Task Handle_TurnTimesOutRatherThanBeingCancelled_EndsSessionAsErrorNotCancelled()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured " +
                                                  "HttpClient.Timeout of 100 seconds elapsing."));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // No cancellation requested — this is the whole point of the case.
        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();

        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                SessionId, SessionStatus.Error, "conversation.unhandled_exception",
                It.IsAny<CancellationToken>()),
            Times.Once);

        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                It.IsAny<Guid>(), SessionStatus.Cancelled, It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_Cancelled_EndsSessionWithAStatusTheSchemaAcceptsAndNamesTheCancellation()
    {
        // Arrange — cancellation surfaces as OperationCanceledException from the pipeline.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // Act
        var act = () => _handler.Handle(command, new CancellationToken(canceled: true));

        // Assert — cancellation is its own terminal state again (#301). It has been three things:
        // the raw literal "cancelled", which the sessions table rejected, so the store swallowed the
        // write and the session stayed open forever; then Error, because no schema change could
        // reach a database that already held data; and now Cancelled, delivered by the migration
        // runner. Asserting the state and not just the reason is the point: the reason is a free-text
        // column nothing filters on, while the status is the column the sessions list and the Grafana
        // $status variable both read.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                SessionId, SessionStatus.Cancelled, "conversation.cancelled", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_Cancelled_EndsSessionWithNonCancelledTokenSoCleanupCompletes()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // Act
        var act = () => _handler.Handle(command, new CancellationToken(canceled: true));

        // Assert — cleanup must not pass the already-cancelled token, otherwise the
        // EndSessionAsync write itself would throw and the session would stay dangling.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                SessionId, SessionStatus.Cancelled, It.IsAny<string?>(),
                It.Is<CancellationToken>(ct => !ct.IsCancellationRequested)),
            Times.Once);
    }

    [Fact]
    public async Task Handle_TurnThrowsException_StillEvictsAgentCache()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = "conv-1",
            UserMessages = ["hello"]
        };

        // Act
        var act = () => _handler.Handle(command, CancellationToken.None);

        // Assert — the finally block runs on the exception path.
        await act.Should().ThrowAsync<InvalidOperationException>();
        _agentCache.Verify(c => c.Evict("conv-1"), Times.Once);
    }

    [Fact]
    public async Task Handle_Success_EndsSessionExactlyOnce()
    {
        // Arrange — a clean run must not double-end the session via the catch path.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult { Success = true, Response = "ok", UpdatedHistory = [] });

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["hello"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(SessionId, SessionStatus.Completed, null, It.IsAny<CancellationToken>()),
            Times.Once);
        _observabilityStore.Verify(
            s => s.EndSessionAsync(
                SessionId, It.IsAny<SessionStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
