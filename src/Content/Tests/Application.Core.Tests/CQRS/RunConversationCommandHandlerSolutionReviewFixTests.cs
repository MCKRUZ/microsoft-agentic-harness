using Application.AI.Common.Interfaces;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
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

        _handler = new RunConversationCommandHandler(
            _mediator.Object,
            _agentCache.Object,
            _observabilityStore.Object,
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
            s => s.EndSessionAsync(SessionId, "error", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
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
                "error",
                It.Is<string?>(r => r != null && !r.Contains("secret") && r.StartsWith("conversation.")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_Cancelled_EndsSessionWithCancelledStatusAndRethrows()
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

        // Assert — cancellation is preserved and the session is ended as cancelled.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _observabilityStore.Verify(
            s => s.EndSessionAsync(SessionId, "cancelled", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
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
                SessionId, "cancelled", It.IsAny<string?>(),
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
            s => s.EndSessionAsync(SessionId, "completed", null, It.IsAny<CancellationToken>()),
            Times.Once);
        _observabilityStore.Verify(
            s => s.EndSessionAsync(SessionId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
