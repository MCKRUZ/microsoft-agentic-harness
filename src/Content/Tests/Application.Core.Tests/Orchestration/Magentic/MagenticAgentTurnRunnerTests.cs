using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using Application.Core.Orchestration.Magentic;
using Application.Core.Tests.Helpers;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Mock<IGovernanceTraceRecorder> _traceRecorder = new();
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();

    private MagenticAgentTurnRunner CreateRunner() => new(
        _agentFactory.Object,
        _agentRegistry.Object,
        _orchestrator.Object,
        _usageCapture.Object,
        _admissionPipeline.Object,
        new ParticipantGovernance(_scopeFactory.Object, _executionContext.Object, _traceRecorder.Object),
        NullLogger<MagenticAgentTurnRunner>.Instance);

    private static AgentDefinition Supervisor(params string[] participantIds) => new()
    {
        Id = "supervisor-agent",
        Name = "Supervisor Agent",
        OrchestrationMode = AgentOrchestrationMode.Magentic,
        Participants = participantIds,
    };

    private static AgentDefinition Participant(string id) => new() { Id = id, Name = id };

    private static MagenticWorkflowResult SuccessResult(string finalOutput = "done") => new()
    {
        WorkflowId = Guid.NewGuid(),
        WorkflowName = "supervisor-agent",
        RoundsExecuted = 1,
        ResetsExecuted = 0,
        PlanReviewsExecuted = 0,
        CompletionReason = "satisfied",
        FinalOutput = finalOutput,
    };

    public MagenticAgentTurnRunnerTests()
    {
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("agent response"), new AgentExecutionContext()));

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

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

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

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

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
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(
                SuccessResult("Here is the synthesized answer.")));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "Summarize the report", [], MagenticTurnOverrides.None, CancellationToken.None);

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
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "Do the task", [], MagenticTurnOverrides.None, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Participants.Should().HaveCount(2);
        captured.Manager.Should().NotBeNull();
        captured.Task.Should().Be("Do the task");
    }

    [Fact]
    public async Task RunTurnAsync_ParticipantsAreWrappedForTheirOwnGovernance_TheManagerIsNot()
    {
        // #769: a participant must be authorized as itself. Microsoft's engine decides when each one runs,
        // so the runner has to hand it participants that arm their own governance when run. The manager is
        // the entry agent the turn is already governed as, so it stays the agent the factory built.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var managerAgent = new TestableAIAgent("manager");
        var participantAgent = new TestableAIAgent("participant");
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<SkillAgentOptions>(o => o.OwningAgentId == "supervisor-agent"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentBuildResult(managerAgent, new AgentExecutionContext()));
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<SkillAgentOptions>(o => o.OwningAgentId == "researcher"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentBuildResult(participantAgent, new AgentExecutionContext()));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        captured!.Manager.Should().BeSameAs(managerAgent);
        captured.Participants.Should().ContainSingle()
            .Which.Should().NotBeSameAs(participantAgent, "it must be wrapped so its runs arm their own governance");
        captured.Participants[0].Name.Should().Be(participantAgent.Name, "Magentic addresses participants by name");
    }

    [Fact]
    public async Task RunTurnAsync_ARunOfAWrappedParticipant_OpensItsOwnGovernanceScope()
    {
        // The behavioural half of the test above: running the participant the runner handed over must
        // actually open a fresh governance scope, not merely be a different object.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var context = new Mock<IAgentExecutionContext>();
        var childPipeline = new Mock<IToolCallAdmissionPipeline>();
        childPipeline.Setup(p => p.GetTrace()).Returns(Domain.AI.Governance.GovernanceTrace.Empty);
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(context.Object);
        provider.Setup(p => p.GetService(typeof(IToolCallAdmissionPipeline))).Returns(childPipeline.Object);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        // The participant runs while the turn is in progress, as the engine would run it; once the runner
        // returns the turn has ended and further runs are refused.
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (MagenticWorkflowRequest req, CancellationToken _) =>
            {
                await req.Participants[0].RunAsync("work");
                return Result<MagenticWorkflowResult>.Success(SuccessResult());
            });

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        context.Verify(
            c => c.Initialize("researcher", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()),
            Times.Once,
            "the participant's own agent id, not the supervisor's, is what its tool calls are authorized as");
        childPipeline.Verify(p => p.Reset(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurnAsync_ReleasesTheParticipantsGovernanceScopesBeforeItReturns(bool orchestratorThrows)
    {
        // #804: a participant's scope now lives for the whole turn, so the runner is what ends it. If it
        // did not, the scope would leak and the participant's trace would never reach the turn's result.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var order = new List<string>();
        var childPipeline = new Mock<IToolCallAdmissionPipeline>();
        childPipeline.Setup(p => p.GetTrace())
            .Callback(() => order.Add("participant trace folded"))
            .Returns(Domain.AI.Governance.GovernanceTrace.Empty);
        _admissionPipeline.Setup(p => p.GetTrace())
            .Callback(() => order.Add("turn trace read"))
            .Returns(Domain.AI.Governance.GovernanceTrace.Empty);
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAgentExecutionContext))).Returns(new Mock<IAgentExecutionContext>().Object);
        provider.Setup(p => p.GetService(typeof(IToolCallAdmissionPipeline))).Returns(childPipeline.Object);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (MagenticWorkflowRequest req, CancellationToken _) =>
            {
                await req.Participants[0].RunAsync("work");
                scope.Verify(s => s.Dispose(), Times.Never, "the scope must outlive the participant's run");
                return orchestratorThrows
                    ? throw new InvalidOperationException("engine failed")
                    : Result<MagenticWorkflowResult>.Success(SuccessResult());
            });

        var act = () => CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        if (orchestratorThrows)
            await act.Should().ThrowAsync<InvalidOperationException>();
        else
            await act();

        scope.Verify(s => s.Dispose(), Times.Once, "the turn ends every participant scope, success or not");
        childPipeline.Verify(p => p.GetTrace(), Times.Once, "the trace is folded once, at release");

        if (!orchestratorThrows)
            order.Should().Equal(
                ["participant trace folded", "turn trace read"],
                "the turn result reports the parent's trace, so participants must be folded into it first");
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
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather?"),
            new(ChatRole.Assistant, "It's sunny."),
        };

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "And tomorrow?", history, MagenticTurnOverrides.None, CancellationToken.None);

        captured!.Task.Should().Contain("What's the weather?");
        captured.Task.Should().Contain("It's sunny.");
        captured.Task.Should().Contain("And tomorrow?");
    }

    [Fact]
    public async Task RunTurnAsync_WorkflowFails_ReturnsGenericFailure_NeverTheRawWorkflowErrorText()
    {
        // Security regression guard: MagenticEventSubscriber/MagenticOrchestrator can put a raw
        // Exception.Message into Result.Errors (paths, hostnames, a tool's own exception text —
        // AgentFactory sets IncludeDetailedErrors unconditionally). This runner is a live, callable
        // turn, so that text must never reach AgentTurnResult.Error verbatim — only a generic message,
        // with the raw detail going to the logger instead.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        const string sensitiveRawError =
            "System.Net.Http.HttpRequestException: could not connect to https://internal-tool-host.corp.local:8443/secret-path (SAS=AKIA...)";
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Fail(sensitiveRawError));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(Application.Core.CQRS.Agents.ExecuteAgentTurn.AgentTurnErrorKind.Internal);
        result.Error.Should().NotBeNullOrEmpty();
        result.Error.Should().NotContain("internal-tool-host");
        result.Error.Should().NotContain("SAS=");
        result.Error.Should().NotContain(sensitiveRawError);
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
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        captured!.MaxRounds.Should().Be(7);
        captured.MaxStalls.Should().Be(1);
        captured.MaxResets.Should().Be(2);
        captured.RequirePlanSignoff.Should().BeTrue();
    }

    [Fact]
    public async Task RunTurnAsync_Overrides_ApplyToManagerSkillOptions_NotToParticipants()
    {
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var seenOptions = new List<SkillAgentOptions>();
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, opts, _) => seenOptions.Add(opts))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("agent response"), new AgentExecutionContext()));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        var overrides = new MagenticTurnOverrides
        {
            SystemPromptOverride = "Extra context for this turn",
            DeploymentOverride = "gpt-4o-mini",
            Temperature = 0.2f,
            TurnContext = "caller-supplied turn context",
        };

        await CreateRunner().RunTurnAsync(supervisor, "conv-1", "hello", [], overrides, CancellationToken.None);

        // Manager built first (call order matches RunTurnAsync's own build sequence).
        seenOptions.Should().HaveCount(2);
        seenOptions[0].AdditionalContext.Should().Be("Extra context for this turn");
        seenOptions[0].DeploymentName.Should().Be("gpt-4o-mini");
        seenOptions[0].Temperature.Should().Be(0.2f);

        // Participant never receives caller-supplied overrides.
        seenOptions[1].AdditionalContext.Should().BeNull();
        seenOptions[1].DeploymentName.Should().BeNull();
        seenOptions[1].Temperature.Should().BeNull();
    }

    [Fact]
    public async Task RunTurnAsync_TurnContext_IsClearedAfterTheWorkflowRuns()
    {
        // The ambient scope must not leak past this turn onto whatever the async flow does next.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        string? seenDuringRun = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => seenDuringRun = CallerTurnContextScope.Current)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        var overrides = new MagenticTurnOverrides { TurnContext = "mood: curious" };

        await CreateRunner().RunTurnAsync(supervisor, "conv-1", "hello", [], overrides, CancellationToken.None);

        seenDuringRun.Should().Be("mood: curious");
        CallerTurnContextScope.Current.Should().BeNull();
    }

    [Fact]
    public async Task RunTurnAsync_WorkflowFailsAfterSpending_ReportsWhatTheWorkflowSpent()
    {
        // #778: a workflow that fails has still paid for every manager and participant call it made, and
        // the conversation budget is charged from this result. This is the normal failure shape — the
        // orchestrator returns a failed Result rather than throwing — so it cannot be left to the
        // handler's catch blocks.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));
        _usageCapture
            .Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(900, 100, 25, 5, "gpt-4o", 0.5m, 0m, []));
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Fail("max stalls reached"));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
        result.CacheRead.Should().Be(25);
        result.CacheWrite.Should().Be(5);
        result.CostUsd.Should().Be(0.5m);
    }

    [Fact]
    public async Task RunTurnAsync_EmptyFinalOutputWithToolActivity_SynthesizesPlaceholderResponse()
    {
        // RunConversationCommandHandler's durable-transcript gate drops a turn whose Response is empty
        // AND ToolCalls is empty — this runner's ToolCalls is always empty (known v1 limitation), so an
        // empty Response here would silently vanish from history despite the workflow having genuinely
        // used tools. A non-empty placeholder keeps the turn storable.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        _usageCapture
            .Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(10, 20, 0, 0, "gpt-4o", 0.01m, 0m, ["search", "calculator"]));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 3,
                ResetsExecuted = 1,
                PlanReviewsExecuted = 0,
                CompletionReason = "round_limit",
                FinalOutput = null,
            }));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().NotBeNullOrWhiteSpace();
        result.Response.Should().Contain("search").And.Contain("calculator");
    }

    [Fact]
    public async Task RunTurnAsync_EmptyFinalOutputWithNoToolActivity_LeavesResponseEmpty()
    {
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        _usageCapture
            .Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(10, 20, 0, 0, "gpt-4o", 0.01m, 0m, []));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(new MagenticWorkflowResult
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "supervisor-agent",
                RoundsExecuted = 3,
                ResetsExecuted = 1,
                PlanReviewsExecuted = 0,
                CompletionReason = "round_limit",
                FinalOutput = null,
            }));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().BeEmpty();
    }

    [Fact]
    public async Task RunTurnAsync_AdmissionPipelineResetThrows_NeverArmsTheAmbientUsageCapture()
    {
        // Regression guard for the ordering fix: Reset() must run BEFORE LlmUsageCapture.Current is
        // armed, so a throwing Reset() leaves nothing armed for a later, unrelated turn to inherit.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));
        _admissionPipeline.Setup(p => p.Reset()).Throws(new InvalidOperationException("boom"));

        LlmUsageCapture.Current = null;

        Func<Task> act = () => CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        LlmUsageCapture.Current.Should().BeNull();
        _orchestrator.Verify(
            o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunTurnAsync_BuildsEveryAgent_WithTheRealConversationIdAsPrerequisiteScope()
    {
        // Regression guard: AgentFactory.ResolvePrerequisiteScope throws for any agent whose skills
        // declare prerequisites unless AdditionalProperties[ConversationIdPropertyKey] carries the
        // real conversation id — without this, a supervisor or participant using such a skill crashed
        // on every turn.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var seenOptions = new List<SkillAgentOptions>();
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, SkillAgentOptions, CancellationToken>(
                (_, opts, _) => seenOptions.Add(opts))
            .ReturnsAsync(new AgentBuildResult(new TestableAIAgent("agent response"), new AgentExecutionContext()));

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "the-real-conversation-id", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        seenOptions.Should().HaveCount(2); // manager + one participant
        foreach (var opts in seenOptions)
        {
            opts.AdditionalProperties.Should().NotBeNull();
            opts.AdditionalProperties![AgentFactory.ConversationIdPropertyKey].Should().Be("the-real-conversation-id");
        }
    }

    [Fact]
    public async Task RunTurnAsync_AgentBuildThrows_ReturnsGracefulFailureInsteadOfPropagating()
    {
        // The prerequisite-scope crash (or any other agent-construction failure) must not abort the
        // whole turn ungracefully — it gets the same Failure shape an unresolvable participant does.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Agent 'researcher' declares skill prerequisites but no conversation scope was supplied."));

        var result = await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(Application.Core.CQRS.Agents.ExecuteAgentTurn.AgentTurnErrorKind.Internal);
        _orchestrator.Verify(
            o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunTurnAsync_ExecutionTracingOn_CompletesAndDisposesEveryBuiltAgentsTraceWriter()
    {
        // Regression guard: MagenticAgentTurnRunner.BuildAgentAsync used to discard the
        // AgentExecutionContext CreateAgentWithContextFromSkillsAsync returns, so a trace writer
        // AgentExecutionContextFactory stashed into it (when MetaHarness.ExecutionTracingEnabled) was
        // never completed or disposed — leaking a file handle and semaphore per agent, per turn. Found
        // by CI's correctness-review after this PR's local review rounds missed it.
        var supervisor = Supervisor("researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        var managerWriter = new Mock<ITraceWriter>();
        managerWriter.Setup(w => w.Scope).Returns(TraceScope.ForExecution(Guid.NewGuid()));
        var participantWriter = new Mock<ITraceWriter>();
        participantWriter.Setup(w => w.Scope).Returns(TraceScope.ForExecution(Guid.NewGuid()));

        var callCount = 0;
        _agentFactory
            .Setup(f => f.CreateAgentWithContextFromSkillsAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var writer = Interlocked.Increment(ref callCount) == 1 ? managerWriter.Object : participantWriter.Object;
                var context = new AgentExecutionContext
                {
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        [ITraceWriter.AdditionalPropertiesKey] = writer,
                    },
                };
                return new AgentBuildResult(new TestableAIAgent("agent response"), context);
            });

        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        managerWriter.Verify(w => w.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
        managerWriter.Verify(w => w.DisposeAsync(), Times.Once);
        participantWriter.Verify(w => w.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
        participantWriter.Verify(w => w.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RunTurnAsync_DuplicateParticipantId_BuildsOnlyOneInstance()
    {
        var supervisor = Supervisor("researcher", "researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        captured!.Participants.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunTurnAsync_ParticipantNamesTheSupervisorItself_SkipsTheSelfReference()
    {
        var supervisor = Supervisor("supervisor-agent", "researcher");
        _agentRegistry.Setup(r => r.TryGet("researcher")).Returns(Participant("researcher"));

        MagenticWorkflowRequest? captured = null;
        _orchestrator
            .Setup(o => o.RunAsync(It.IsAny<MagenticWorkflowRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MagenticWorkflowRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Result<MagenticWorkflowResult>.Success(SuccessResult()));

        await CreateRunner().RunTurnAsync(
            supervisor, "conv-1", "hello", [], MagenticTurnOverrides.None, CancellationToken.None);

        captured!.Participants.Should().HaveCount(1);
    }
}
