using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.AI.Common.Services.AI;
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

public class RunConversationCommandHandlerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly RunConversationCommandHandler _handler;

    public RunConversationCommandHandlerTests()
    {
        // Budget disabled by default — these tests don't exercise the conversation budget.
        _budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);

        // Store and lease are strict about being unused here: every test in this class runs a
        // self-contained conversation (no ConversationOwnerId), and touching either would mean the
        // handler had silently taken the durable path. A throwing double says so immediately.
        var observability = new Mock<IObservabilityStore>().Object;
        var strictStore = new Mock<IConversationStore>(MockBehavior.Strict).Object;

        _handler = new RunConversationCommandHandler(
            _mediator.Object,
            new Mock<IAgentConversationCache>().Object,
            _budget.Object,
            observability,
            // The strict store is shared with the recorder deliberately: a self-contained run must not
            // touch it, and the recorder is now the thing that would.
            new ConversationTelemetryRecorder(
                observability, strictStore, NullLogger<ConversationTelemetryRecorder>.Instance),
            strictStore,
            new Mock<IConversationTurnLease>(MockBehavior.Strict).Object,
            Options.Create(new ConversationsConfig()),
            NullLogger<RunConversationCommandHandler>.Instance);
    }

    private static AgentTurnResult SuccessTurn(string response, IReadOnlyList<string>? tools = null) => new()
    {
        Success = true,
        Response = response,
        UpdatedHistory = [new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, response)],
        ToolsInvoked = tools ?? []
    };

    private static AgentTurnResult FailedTurn(string error) => new()
    {
        Success = false,
        Response = string.Empty,
        UpdatedHistory = [new ChatMessage(ChatRole.User, "q")],
        Error = error
    };

    [Fact]
    public async Task Handle_SingleMessage_ExecutesOneTurn()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("Hello back"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["Hello"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().HaveCount(1);
        result.FinalResponse.Should().Be("Hello back");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ConversationBudgetExhausted_StopsGracefullyBeforeNextTurn()
    {
        // First turn's gate sees budget available; after it runs, the next gate sees it exhausted.
        _budget.SetupSequence(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled)              // turn 1 gate → allowed
            .ReturnsAsync(new ConversationBudgetStatus(true, 100, 100));  // turn 2 gate → exhausted

        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("answer"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = "conv-budget",
            UserMessages = ["one", "two", "three"],
            MaxTurns = 10
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue("a budget stop is graceful, not a failure");
        result.BudgetExhausted.Should().BeTrue();
        result.Turns.Should().HaveCount(1, "only the first turn ran before the budget gate tripped");
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RecordsUsageAgainstTheConversation()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("answer"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = "conv-release",
            UserMessages = ["one"]
        };

        await _handler.Handle(command, CancellationToken.None);

        _budget.Verify(
            b => b.RecordUsageAsync("conv-release", It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The budget spans the whole conversation, and a conversation now outlives one run and one host
    /// (issue #235). This handler used to release in a <c>finally</c>, which reset the accumulated
    /// total every run and turned a lifetime ceiling into a per-run one — silently, because the run
    /// that erased it was also the run that succeeded.
    /// </summary>
    [Fact]
    public async Task Handle_NeverReleasesTheConversationBudget_SoATotalSurvivesTheRun()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("answer"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = "conv-survives",
            UserMessages = ["one"]
        };

        await _handler.Handle(command, CancellationToken.None);

        _budget.Verify(
            b => b.ReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The same guarantee on the failure path, which is where the old <c>finally</c> lived: a run that
    /// throws must not take the conversation's accumulated spend with it, or a caller could reset a
    /// ceiling by failing repeatedly.
    /// </summary>
    [Fact]
    public async Task Handle_ThrowingTurn_StillNeverReleasesTheConversationBudget()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("turn exploded"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            ConversationId = "conv-throws",
            UserMessages = ["one"]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));

        _budget.Verify(
            b => b.ReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SingleMessage_TurnSummaryHasCorrectData()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("Response"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["My question"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Turns[0].TurnNumber.Should().Be(1);
        result.Turns[0].UserMessage.Should().Be("My question");
        result.Turns[0].AgentResponse.Should().Be("Response");
    }

    [Fact]
    public async Task Handle_TurnCancelled_PropagatesCancellation_NotAFailedResult()
    {
        // A turn tagged Cancelled (e.g. caller disconnect) is routine: it must surface as
        // an OperationCanceledException (session ended "cancelled"), not a failed result.
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "cancelled",
                ErrorKind = AgentTurnErrorKind.Cancelled,
            });

        var command = new RunConversationCommand { AgentName = "TestAgent", UserMessages = ["Hello"] };

        var act = () => _handler.Handle(command, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_MultipleMessages_ExecutesMultipleTurns()
    {
        // Arrange
        var callCount = 0;
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return SuccessTurn($"Reply {callCount}");
            });

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["First", "Second", "Third"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().HaveCount(3);
        result.FinalResponse.Should().Be("Reply 3");
    }

    [Fact]
    public async Task Handle_MultipleMessages_FeedsHistoryFromPreviousTurn()
    {
        // Arrange
        var capturedCommands = new List<ExecuteAgentTurnCommand>();
        var turnHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "msg1"),
            new(ChatRole.Assistant, "reply1")
        };

        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<AgentTurnResult>, CancellationToken>((req, _) =>
                capturedCommands.Add((ExecuteAgentTurnCommand)req))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = "reply",
                UpdatedHistory = turnHistory
            });

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["msg1", "msg2"]
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedCommands.Should().HaveCount(2);
        capturedCommands[0].ConversationHistory.Should().BeEmpty();
        capturedCommands[1].ConversationHistory.Should().BeEquivalentTo(turnHistory);
    }

    [Fact]
    public async Task Handle_MultipleMessages_TurnNumbersAreSequential()
    {
        // Arrange
        var capturedCommands = new List<ExecuteAgentTurnCommand>();
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<AgentTurnResult>, CancellationToken>((req, _) =>
                capturedCommands.Add((ExecuteAgentTurnCommand)req))
            .ReturnsAsync(SuccessTurn("ok"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["a", "b", "c"]
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedCommands.Select(c => c.TurnNumber).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Handle_MaxTurnsReached_StopsEarly()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("ok"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["a", "b", "c", "d", "e"],
            MaxTurns = 2
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().HaveCount(2);
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_TurnFails_ReturnsFailureImmediately()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedTurn("Model error"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["a", "b", "c"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Turn 1 failed");
        result.Error.Should().Contain("Model error");
        result.FinalResponse.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SecondTurnFails_FirstTurnStillInResults()
    {
        // Arrange
        var callCount = 0;
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? SuccessTurn("good") : FailedTurn("bad");
            });

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["a", "b"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Turns.Should().HaveCount(1);
        result.Turns[0].AgentResponse.Should().Be("good");
        result.Error.Should().Contain("Turn 2 failed");
    }

    [Fact]
    public async Task Handle_TurnFails_DoesNotExecuteSubsequentTurns()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedTurn("error"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["a", "b", "c"]
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_WithProgressCallback_InvokesCallbackForEachTurn()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("ok"));

        var progressUpdates = new List<TurnProgress>();
        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["msg1", "msg2"],
            OnProgress = progress =>
            {
                progressUpdates.Add(progress);
                return Task.CompletedTask;
            }
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert — 2 messages x 2 callbacks each (executing + completed)
        progressUpdates.Should().HaveCount(4);
        progressUpdates[0].Status.Should().Be("executing");
        progressUpdates[1].Status.Should().Be("completed");
        progressUpdates[2].Status.Should().Be("executing");
        progressUpdates[3].Status.Should().Be("completed");
    }

    [Fact]
    public async Task Handle_WithProgressCallback_CompletedCallbackIncludesResponse()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("agent reply"));

        var progressUpdates = new List<TurnProgress>();
        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["msg"],
            OnProgress = progress =>
            {
                progressUpdates.Add(progress);
                return Task.CompletedTask;
            }
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        var completedUpdate = progressUpdates.Single(p => p.Status == "completed");
        completedUpdate.Response.Should().Be("agent reply");
        completedUpdate.AgentName.Should().Be("TestAgent");
    }

    [Fact]
    public async Task Handle_WithoutProgressCallback_DoesNotThrow()
    {
        // Arrange
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTurn("ok"));

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["msg"],
            OnProgress = null
        };

        // Act
        var act = () => _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_TracksToolInvocations_AccumulatesAcrossTurns()
    {
        // Arrange
        var callCount = 0;
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? SuccessTurn("r1", ["tool_a", "tool_b"])
                    : SuccessTurn("r2", ["tool_c"]);
            });

        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["m1", "m2"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.TotalToolInvocations.Should().Be(3);
        result.Turns[0].ToolsInvoked.Should().Equal("tool_a", "tool_b");
        result.Turns[1].ToolsInvoked.Should().Equal("tool_c");
    }

    [Fact]
    public async Task Handle_NoMessages_ReturnsEmptySuccess()
    {
        // Arrange
        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = []
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().BeEmpty();
        result.FinalResponse.Should().BeEmpty();
        result.TotalToolInvocations.Should().Be(0);
    }

    [Fact]
    public async Task Handle_PassesConversationIdToTurnCommands()
    {
        // Arrange
        var capturedCommands = new List<ExecuteAgentTurnCommand>();
        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<AgentTurnResult>, CancellationToken>((req, _) =>
                capturedCommands.Add((ExecuteAgentTurnCommand)req))
            .ReturnsAsync(SuccessTurn("ok"));

        var conversationId = "test-conv-123";
        var command = new RunConversationCommand
        {
            AgentName = "TestAgent",
            UserMessages = ["msg"],
            ConversationId = conversationId
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedCommands.Should().ContainSingle()
            .Which.ConversationId.Should().Be(conversationId);
    }
}
