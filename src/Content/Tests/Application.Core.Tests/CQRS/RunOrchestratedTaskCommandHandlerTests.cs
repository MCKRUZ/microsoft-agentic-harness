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

public class RunOrchestratedTaskCommandHandlerTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline _admissionPipeline =
        Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
            p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty);
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
            new Application.AI.Common.Services.Agent.AgentExecutionContext(),
            _admissionPipeline,
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
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

    /// <summary>The orchestrator's reply for a plan: the typed JSON the planning call asks for.</summary>
    private static string Plan(params (string Agent, string Description)[] subtasks) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            subtasks = subtasks.Select(t => new { agent = t.Agent, description = t.Description }),
        });

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
        var agent = CreateOrchestratorAgent(Plan(("AgentA", "Do thing one")));
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
        var planText = Plan(("AgentA", "Analyze the code"), ("AgentB", "Write the tests"));
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
        var planText = Plan(("AgentA", "Do work"));
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
        var planText = Plan(("AgentA", "Task 1"), ("AgentB", "Task 2"), ("AgentA", "Task 3"));
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
        var planText = Plan(("AgentA", "Do failing work"));
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
    public async Task Handle_PlanIsNotValidJsonEvenAfterRepair_FailsWithAStableCode_AndRunsNoSubAgent()
    {
        // The old parser fell back to handing the whole task to the first agent when it found no SUBTASK
        // lines, which turned "the model produced something unusable" into a confident wrong answer. A plan
        // that cannot be read, after the one repair attempt, is now a failure.
        var calls = 0;
        var agent = new TestableAIAgent((_, _) =>
        {
            calls++;
            return Task.FromResult(new AgentResponse(new ChatMessage(
                ChatRole.Assistant, "I think we should analyze the codebase thoroughly.")));
        });
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanInvalid);
        result.SubAgentResults.Should().BeEmpty();
        calls.Should().Be(2, "the first reply plus the one repair attempt, and nothing after");
        _mediator.Verify(
            m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FirstPlanIsMalformed_IsRepairedOnce_ThenRuns()
    {
        var calls = 0;
        var agent = new TestableAIAgent((_, _) =>
        {
            calls++;
            var text = calls switch
            {
                1 => "Sure! SUBTASK: AgentA - not the format asked for",
                2 => Plan(("AgentA", "Do the work")),
                _ => "Final synthesis",
            };
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)));
        });
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SubAgentResults.Should().ContainSingle().Which.Subtask.Should().Be("Do the work");
    }

    [Fact]
    public async Task Handle_PlanningCall_CarriesThePlanSchema_AndRunsUnderTheHandlersAdmissionPipeline()
    {
        // The typed plan only exists if the schema reaches the model, and the planning call is a tool-
        // capable agent run, so it must be governed like the synthesis call is.
        var agent = new PlanSpyAgent(Plan(("AgentA", "Do the work")));
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        agent.FirstCallResponseFormat.Should().BeOfType<ChatResponseFormatJson>()
            .Which.SchemaName.Should().Be("orchestration_plan");
        agent.FirstCallAmbientPipeline.Should().BeSameAs(_admissionPipeline);
    }

    [Theory]
    [InlineData("{\"subtasks\":null}")]
    [InlineData("{\"subtasks\":[null]}")]
    [InlineData("{\"subtasks\":[{\"agent\":null,\"description\":\"x\"}]}")]
    public async Task Handle_PlanJsonCarriesNulls_FailsWithAStableCode_NotARawException(string planJson)
    {
        // "required" only demands the property be present, so a null list, a null element or a null agent
        // can still deserialize. Reading one must end in the stable code, not a NullReferenceException that
        // the handler's catch-all would return as raw exception text.
        var agent = CreateOrchestratorAgent(planJson);
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("orchestration.plan_");
    }

    [Fact]
    public async Task Handle_PlanningCallFails_ReportsItAsUnavailable_NotAsABadPlan_AndLeaksNoDetail()
    {
        // A provider that rejects the schema request, a content-safety block or a network fault is not a
        // model that wrote a bad plan, and its message can carry endpoints and tokens.
        var agent = new TestableAIAgent((_, _) =>
            throw new InvalidOperationException("secret endpoint https://internal.example/token=abc"));
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanUnavailable);
    }

    [Fact]
    public async Task Handle_PlanHasNoSubtasks_FailsWithAStableCode()
    {
        var agent = CreateOrchestratorAgent(Plan());
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanEmpty);
    }

    [Fact]
    public async Task Handle_PlanNamesAnAgentThatIsNotAvailable_FailsWithAStableCode_AndRunsNothing()
    {
        // Skipping the unknown subtask and running the rest would synthesize an answer that silently
        // omits part of the work, so a plan naming an agent that does not exist is rejected whole.
        var agent = CreateOrchestratorAgent(
            Plan(("UnknownAgent", "This cannot run"), ("AgentA", "This could run")));
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillAsync(
                It.IsAny<string>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        SetupMediatorForConversation();

        var result = await _handler.Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(OrchestrationErrors.PlanUnknownAgent);
        result.SubAgentResults.Should().BeEmpty();
        _mediator.Verify(
            m => m.Send(It.IsAny<RunConversationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SynthesizesResultsThroughOrchestrator()
    {
        // Arrange
        var planText = Plan(("AgentA", "Analyze code"));
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
        var planText = Plan(("AgentA", "Work"));
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
        var planText = Plan(("AgentA", "Task 1"), ("AgentB", "Task 2"));
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
        // Arrange -- the plan uses different casing than the available agents
        var planText = Plan(("agenta", "Work to do"));
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
            ConversationId = "shared-conv-id"
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mediator.Verify(m => m.Send(
            It.Is<RunConversationCommand>(c => c.ConversationId == "shared-conv-id"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// An orchestrator that answers the first call with a plan and later calls with a synthesis, recording
    /// what the planning call was sent with and under.
    /// </summary>
    private sealed class PlanSpyAgent(string planJson) : AIAgent
    {
        private int _calls;

        public ChatResponseFormat? FirstCallResponseFormat { get; private set; }

        public Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline? FirstCallAmbientPipeline { get; private set; }

        protected override string IdCore => "plan-spy";

        public override string? Name => "plan-spy";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            if (_calls++ == 0)
            {
                FirstCallResponseFormat = options?.ResponseFormat;
                FirstCallAmbientPipeline = Application.AI.Common.Services.Governance.ToolAdmissionAccessor.Current;
            }

            var text = _calls == 1 ? planJson : "Final synthesis";
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
