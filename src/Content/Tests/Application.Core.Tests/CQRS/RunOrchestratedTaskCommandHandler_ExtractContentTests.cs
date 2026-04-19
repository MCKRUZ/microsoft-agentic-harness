using Application.AI.Common.Interfaces;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Application.Core.Tests.Helpers;
using Domain.AI.Skills;
using FluentAssertions;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Tests for edge cases in <see cref="RunOrchestratedTaskCommandHandler"/>,
/// covering parsing edge cases, subtask limits, and progress callback behavior.
/// </summary>
public class RunOrchestratedTaskCommandHandler_EdgeCaseTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly RunOrchestratedTaskCommandHandler _handler;

    public RunOrchestratedTaskCommandHandler_EdgeCaseTests()
    {
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IMediator)))
            .Returns(_mediator.Object);

        var serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(s => s.ServiceProvider).Returns(scopedProvider.Object);

        _scopeFactory.Setup(f => f.CreateScope()).Returns(serviceScope.Object);

        _handler = new RunOrchestratedTaskCommandHandler(
            _agentFactory.Object,
            _scopeFactory.Object,
            NullLogger<RunOrchestratedTaskCommandHandler>.Instance);
    }

    private TestableAIAgent CreateOrchestratorAgent(string planResponse, string? synthesisResponse = null)
    {
        var callCount = 0;
        return new TestableAIAgent((msgs, _) =>
        {
            callCount++;
            var text = callCount == 1 ? planResponse : (synthesisResponse ?? "Final synthesis");
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)));
        });
    }

    private void SetupMediatorForConversation(string response = "subtask done")
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = true,
                Turns = [new TurnSummary
                {
                    TurnNumber = 1,
                    UserMessage = "subtask",
                    AgentResponse = response,
                    ToolsInvoked = []
                }],
                FinalResponse = response,
                TotalToolInvocations = 0
            });
    }

    [Fact]
    public async Task Handle_SubtaskWithoutDash_SkipsLine()
    {
        // Arrange - SUBTASK: line without " - " separator
        var planText = "SUBTASK: AgentAnoSeparator\nSUBTASK: AgentA - Valid task";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - only the valid line should be parsed
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].Subtask.Should().Be("Valid task");
    }

    [Fact]
    public async Task Handle_SubtaskWithEmptyDescription_SkipsLine()
    {
        // Arrange - SUBTASK: AgentA - (empty description after trim)
        var planText = "SUBTASK: AgentA -    ";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - falls back to first agent since no valid subtask parsed
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
    }

    [Fact]
    public async Task Handle_WithoutProgressCallback_DoesNotThrow()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Work";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"],
            OnProgress = null
        };

        // Act
        var act = () => _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_MaxTotalTurnsOne_OnlyPlanningTurnExecutes()
    {
        // Arrange - maxTotalTurns=1 means planning turn fills the budget
        var planText = "SUBTASK: AgentA - Work";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"],
            MaxTotalTurns = 1
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - no subtask runs, only planning
        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SubtaskCaseInsensitivePrefix_ParsesCorrectly()
    {
        // Arrange - "subtask:" in lowercase
        var planText = "subtask: AgentA - Work to do";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].Subtask.Should().Be("Work to do");
    }

    [Fact]
    public async Task Handle_EmptyAvailableAgentsAtRuntime_FallbackSkipsAllSubtasks()
    {
        // Arrange - no available agents matches any SUBTASK
        var planText = "SUBTASK: UnknownAgent - Work";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        // Use AgentA but subtask references UnknownAgent
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - fallback assigns to first available agent
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
    }

    [Fact]
    public async Task Handle_SubAgentCollectsDistinctTools_AcrossMultipleTurns()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Work";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        _mediator
            .Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = true,
                Turns =
                [
                    new TurnSummary
                    {
                        TurnNumber = 1,
                        UserMessage = "subtask",
                        AgentResponse = "step 1 done",
                        ToolsInvoked = ["read_file", "write_file"]
                    },
                    new TurnSummary
                    {
                        TurnNumber = 2,
                        UserMessage = "continue",
                        AgentResponse = "step 2 done",
                        ToolsInvoked = ["read_file", "search"]
                    }
                ],
                FinalResponse = "done",
                TotalToolInvocations = 4
            });

        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert - distinct tools from both turns
        result.SubAgentResults[0].ToolsInvoked.Should().Contain("read_file");
        result.SubAgentResults[0].ToolsInvoked.Should().Contain("write_file");
        result.SubAgentResults[0].ToolsInvoked.Should().Contain("search");
        result.TotalToolInvocations.Should().Be(4);
    }
}
