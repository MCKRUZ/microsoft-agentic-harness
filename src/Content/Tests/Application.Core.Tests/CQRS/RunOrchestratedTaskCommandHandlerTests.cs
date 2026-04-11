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

public class RunOrchestratedTaskCommandHandlerTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly RunOrchestratedTaskCommandHandler _handler;

    public RunOrchestratedTaskCommandHandlerTests()
    {
        // Wire: scopeFactory → scope → serviceProvider → mediator
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

    private static RunOrchestratedTaskCommand CreateCommand(
        string orchestratorName = "Orchestrator",
        string taskDescription = "Do something",
        IReadOnlyList<string>? availableAgents = null,
        int maxTotalTurns = 20) => new()
    {
        OrchestratorName = orchestratorName,
        TaskDescription = taskDescription,
        AvailableAgents = availableAgents ?? ["AgentA", "AgentB"],
        MaxTotalTurns = maxTotalTurns
    };

    private static TestableAIAgent CreateOrchestratorAgent(string planResponse, string? synthesisResponse = null)
    {
        var callCount = 0;
        return new TestableAIAgent((msgs, _) =>
        {
            callCount++;
            var text = callCount == 1 ? planResponse : (synthesisResponse ?? "Final synthesis");
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)));
        });
    }

    private void SetupMediatorForConversation(string response = "subtask done", bool success = true)
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResult
            {
                Success = success,
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
    public async Task Handle_ValidRequest_CreatesOrchestratorAgent()
    {
        // Arrange
        var agent = CreateOrchestratorAgent("SUBTASK: AgentA - Do thing one");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                "Orchestrator",
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _agentFactory.Verify(f => f.CreateAgentFromSkillAsync(
            "Orchestrator",
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_OrchestratorDecomposes_DelegatesSubtasks()
    {
        // Arrange
        var planText = """
            SUBTASK: AgentA - Analyze the code
            SUBTASK: AgentB - Write the tests
            """;
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().HaveCount(2);
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
        result.SubAgentResults[0].Subtask.Should().Be("Analyze the code");
        result.SubAgentResults[1].AgentName.Should().Be("AgentB");
        result.SubAgentResults[1].Subtask.Should().Be("Write the tests");
    }

    [Fact]
    public async Task Handle_OrchestratorDecomposes_DelegatesViaMediator()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Do work";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mediator.Verify(m => m.Send(
            It.Is<RunConversationCommand>(c =>
                c.AgentName == "AgentA" &&
                c.UserMessages.Count == 1 &&
                c.UserMessages[0] == "Do work"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AgentFactoryThrows_ReturnsFailure()
    {
        // Arrange
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No orchestrator"));

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.FinalSynthesis.Should().BeEmpty();
        result.SubAgentResults.Should().BeEmpty();
        result.Error.Should().Be("No orchestrator");
    }

    [Fact]
    public async Task Handle_MaxTotalTurnsReached_StopsEarly()
    {
        // Arrange -- plan produces 3 subtasks but maxTotalTurns only allows 2 (1 plan + 1 subtask)
        var planText = """
            SUBTASK: AgentA - Task 1
            SUBTASK: AgentB - Task 2
            SUBTASK: AgentA - Task 3
            """;
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand(maxTotalTurns: 2);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SubAgentResults.Count.Should().BeLessThan(3);
    }

    [Fact]
    public async Task Handle_SubAgentFails_IncludesInResults()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Do failing work";
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
                Success = false,
                Turns = [],
                FinalResponse = string.Empty,
                Error = "Sub-agent crashed"
            });

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert -- orchestration still succeeds; the failed sub-agent is recorded
        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].Success.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NoSubtasksParsed_FallsBackToFirstAgent()
    {
        // Arrange -- orchestrator returns text without SUBTASK: format
        var planText = "I think we should analyze the codebase thoroughly.";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert -- falls back to assigning entire plan text to first available agent
        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
    }

    [Fact]
    public async Task Handle_SubtaskReferencesUnavailableAgent_SkipsIt()
    {
        // Arrange
        var planText = """
            SUBTASK: UnknownAgent - This should be skipped
            SUBTASK: AgentA - This should run
            """;
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert -- only AgentA subtask runs
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
    }

    [Fact]
    public async Task Handle_SynthesizesResultsThroughOrchestrator()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Analyze code";
        var agent = CreateOrchestratorAgent(planText, "Combined analysis complete.");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.FinalSynthesis.Should().Be("Combined analysis complete.");
    }

    [Fact]
    public async Task Handle_WithProgressCallback_ReportsAllPhases()
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

        var progressUpdates = new List<OrchestrationProgress>();
        var command = new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Do work",
            AvailableAgents = ["AgentA"],
            OnProgress = progress =>
            {
                progressUpdates.Add(progress);
                return Task.CompletedTask;
            }
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert -- should have planning, delegation, and synthesis phases
        progressUpdates.Should().Contain(p => p.Phase == "planning");
        progressUpdates.Should().Contain(p => p.Phase == "delegation");
        progressUpdates.Should().Contain(p => p.Phase == "synthesis");
    }

    [Fact]
    public async Task Handle_AccumulatesTotalTurnsAndToolInvocations()
    {
        // Arrange
        var planText = "SUBTASK: AgentA - Task 1\nSUBTASK: AgentB - Task 2";
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
                Turns = [
                    new TurnSummary
                    {
                        TurnNumber = 1,
                        UserMessage = "task",
                        AgentResponse = "done",
                        ToolsInvoked = ["tool1"]
                    }
                ],
                FinalResponse = "done",
                TotalToolInvocations = 1
            });

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert -- 1 planning + 2 subtask turns (1 each) + 1 synthesis = 4
        result.TotalTurns.Should().Be(4);
        result.TotalToolInvocations.Should().Be(2);
    }

    [Fact]
    public async Task Handle_CaseInsensitiveAgentMatching_MatchesCorrectly()
    {
        // Arrange -- SUBTASK uses different casing than available agents
        var planText = "SUBTASK: agenta - Work to do";
        var agent = CreateOrchestratorAgent(planText);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var command = CreateCommand(availableAgents: ["AgentA", "AgentB"]);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert -- should match case-insensitively and use the original name
        result.SubAgentResults.Should().ContainSingle();
        result.SubAgentResults[0].AgentName.Should().Be("AgentA");
    }

    [Fact]
    public async Task Handle_PassesConversationIdToSubTasks()
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
            ConversationId = "shared-conv-id"
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mediator.Verify(m => m.Send(
            It.Is<RunConversationCommand>(c => c.ConversationId == "shared-conv-id"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
