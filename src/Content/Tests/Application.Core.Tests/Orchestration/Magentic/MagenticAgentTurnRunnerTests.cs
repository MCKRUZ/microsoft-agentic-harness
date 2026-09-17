using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.Core.Orchestration.Magentic;
using Application.Core.Tests.Helpers;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.Orchestration.Magentic;

/// <summary>
/// Unit tests for <see cref="MagenticAgentTurnRunner"/> — the collaborator
/// <see cref="Application.Core.CQRS.Agents.ExecuteAgentTurn.ExecuteAgentTurnCommandHandler"/> delegates
/// to for a <see cref="AgentOrchestrationMode.Magentic"/> supervisor's turn.
/// </summary>
public sealed class MagenticAgentTurnRunnerTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly Mock<IMagenticOrchestrator> _orchestrator = new();
    private readonly Mock<ILlmUsageCapture> _usageCapture = new();
    private readonly Mock<IToolCallAdmissionPipeline> _admissionPipeline = new();

    private MagenticAgentTurnRunner CreateRunner() => new(
        _agentFactory.Object,
        _agentRegistry.Object,
        _orchestrator.Object,
        _usageCapture.Object,
        _admissionPipeline.Object,
        NullLogger<MagenticAgentTurnRunner>.Instance);

    private static AgentDefinition Supervisor(params string[] participantIds) => new()
    {
        Id = "supervisor-agent",
        Name = "Supervisor Agent",
        OrchestrationMode = AgentOrchestrationMode.Magentic,
        Participants = participantIds,
    };

    private static AgentDefinition Participant(string id) => new() { Id = id, Name = id };

    public MagenticAgentTurnRunnerTests()
    {
        _agentFactory
            .Setup(f => f.CreateAgentFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent("agent response"));

        _usageCapture
            .Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(10, 20, 0, 0, "gpt-4o", 0.01m, 0m, ["search"]));

        _admissionPipeline
            .Setup(p => p.GetTrace())
            .Returns(Domain.AI.Governance.GovernanceTrace.Empty);
    }

    [Fact]
    public async Task RunTurnAsync_NoParticipantsDeclared_ReturnsFailureWithoutCallingOrchestrator()
    {
        var supervisor = Supervisor(); // no participants

        var result = await CreateRunner().RunTurnAsync(supervisor, "hello", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(Application.Core.CQRS.Agents.ExecuteAgentTurn.AgentTurnErrorKind.Internal);
        _orchestrator.Verify(
            o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunTurnAsync_UnresolvableParticipants_ReturnsFailureWithoutCallingOrchestrator()
    {
        var supervisor = Supervisor("ghost-participant");
        _agentRegistry.Setup(r => r.TryGet("ghost-participant")).Returns((AgentDefinition?)null);

        var result = await CreateRunner().RunTurnAsync(supervisor, "hello", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        _orchestrator.Verify(
            o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunTurnAsync_SuccessfulWorkflow_MapsResultAndUsageIntoAgentTurnResult()
    {
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 3,
                ResetsExecuted = 0,
                PlanReviewsExecuted = 0,
                CompletionReason = "satisfied",
                FinalOutput = "Here is the synthesized answer.",
            }));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "Summarize the report", [], CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().Be("Here is the synthesized answer.");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(20);
        result.CostUsd.Should().Be(0.01m);
        result.Model.Should().Be("gpt-4o");
        result.ToolsInvoked.Should().BeEquivalentTo(["search"]);
        result.UpdatedHistory.Should().HaveCount(2);
        result.UpdatedHistory[0].Role.Should().Be(ChatRole.User);
        result.UpdatedHistory[1].Role.Should().Be(ChatRole.Assistant);
    }

    [Fact]
    public async Task RunTurnAsync_WorkflowRequest_CarriesManagerAndEveryResolvedParticipant()
    {
        var supervisor = Supervisor("researcher", "writer");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));
        _agentRegistry.Setup(r => r.TryGet("writer")).Returns(Participant("writer"));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 1,
                ResetsExecuted = 0,
                PlanReviewsExecuted = 0,
                CompletionReason = "satisfied",
                FinalOutput = "done",
            }));

        await CreateRunner().RunTurnAsync(supervisor, "Do the task", [], CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Participants.Should().HaveCount(2);
        captured.Manager.Should().NotBeNull();
        captured.Task.Should().Be("Do the task");
    }

    [Fact]
    public async Task RunTurnAsync_WithConversationHistory_FoldsPriorTurnsIntoTaskAsPreamble()
    {
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 1,
                ResetsExecuted = 0,
                PlanReviewsExecuted = 0,
                CompletionReason = "satisfied",
                FinalOutput = "done",
            }));

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather?"),
            new(ChatRole.Assistant, "It's sunny."),
        };

        await CreateRunner().RunTurnAsync(supervisor, "And tomorrow?", history, CancellationToken.None);

        captured!.Task.Should().Contain("What's the weather?");
        captured.Task.Should().Contain("It's sunny.");
        captured.Task.Should().Contain("And tomorrow?");
    }

    [Fact]
    public async Task RunTurnAsync_WorkflowFails_ReturnsFailureWithErrorMessage()
    {
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Fail("magentic.round_limit_exceeded"));

        var result = await CreateRunner().RunTurnAsync(supervisor, "hello", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("magentic.round_limit_exceeded");
        result.ErrorKind.Should().Be(Application.Core.CQRS.Agents.ExecuteAgentTurn.AgentTurnErrorKind.Internal);
    }

    [Fact]
    public async Task RunTurnAsync_UsesMagenticOptions_WhenSupervisorDeclaresTuning()
    {
        var supervisor = Supervisor("researcher") with
        {
            MagenticOptions = new MagenticAgentOptions
            {
                MaxRounds = 7,
                MaxStalls = 1,
                MaxResets = 2,
                RequirePlanSignoff = true,
            },
        };
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 1,
                ResetsExecuted = 0,
                PlanReviewsExecuted = 0,
                CompletionReason = "satisfied",
                FinalOutput = "done",
            }));

        await CreateRunner().RunTurnAsync(supervisor, "hello", [], CancellationToken.None);

        captured!.MaxRounds.Should().Be(7);
        captured.MaxStalls.Should().Be(1);
        captured.MaxResets.Should().Be(2);
        captured.RequirePlanSignoff.Should().BeTrue();
    }
}
