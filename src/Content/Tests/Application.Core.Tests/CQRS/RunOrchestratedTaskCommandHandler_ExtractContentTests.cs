using Application.AI.Common.Interfaces;
using Application.AI.Common.StructuredOutput;
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
/// covering plan validation, subtask limits, and progress callback behavior.
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
            new Application.AI.Common.Services.Agent.AgentExecutionContext(),
            Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
                p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty),
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
            NullLogger<RunOrchestratedTaskCommandHandler>.Instance);
    }

    /// <summary>The orchestrator's reply for a plan: the typed JSON the planning call asks for.</summary>
    private static string Plan(params (string Agent, string Description)[] subtasks) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            subtasks = subtasks.Select(t => new { agent = t.Agent, description = t.Description }),
        });

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
    public async Task Handle_SubtaskMissingItsDescriptionField_IsAnInvalidPlan()
    {
        // Required members are enforced by the schema, so a subtask without a description fails the
        // parse (and the one repair attempt) instead of being skipped.
        var agent = CreateOrchestratorAgent("{\"subtasks\":[{\"agent\":\"AgentA\"}]}");
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanInvalid);
    }

    [Fact]
    public async Task Handle_SubtaskWithABlankDescription_IsAnInvalidPlan()
    {
        // A subtask with nothing to do is not a plan the agent could act on; it was silently skipped
        // (and the whole task handed to the first agent) before plans were typed.
        var agent = CreateOrchestratorAgent(Plan(("AgentA", "   ")));
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(new RunOrchestratedTaskCommand
        {
            OrchestratorName = "Orchestrator",
            TaskDescription = "Test",
            AvailableAgents = ["AgentA"]
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanInvalid);
        result.SubAgentResults.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithoutProgressCallback_DoesNotThrow()
    {
        // Arrange
        var planText = Plan(("AgentA", "Work"));
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
        var planText = Plan(("AgentA", "Work"));
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
    public async Task Handle_SubAgentCollectsDistinctTools_AcrossMultipleTurns()
    {
        // Arrange
        var planText = Plan(("AgentA", "Work"));
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
