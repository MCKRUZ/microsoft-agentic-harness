using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Notifications;
using Application.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Orchestration.Magentic;
using Application.Core.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Verifies <see cref="ExecuteAgentTurnCommandHandler"/>'s single chokepoint branch: a request
/// naming an <see cref="AgentOrchestrationMode.Magentic"/> supervisor delegates to
/// <see cref="IMagenticAgentTurnRunner"/> instead of the single-agent cache path, and every live
/// caller (this handler is the one all of them funnel through) gets that behaviour automatically.
/// </summary>
public sealed class ExecuteAgentTurnCommandHandler_MagenticTests
{
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly Mock<IMagenticAgentTurnRunner> _magenticTurnRunner = new();
    private readonly Mock<IObservabilityStore> _observability = new();

    private ExecuteAgentTurnCommandHandler CreateHandler()
    {
        var usageCapture = new Mock<ILlmUsageCapture>();
        usageCapture.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>()));

        return new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
                p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty),
            _agentRegistry.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            new Application.AI.Common.Services.Context.ConversationRegistrationTracker(),
            _observability.Object,
            usageCapture.Object,
            new DefaultContextSnapshotComputer(),
            new NullContextSnapshotNotifier(),
            TimeProvider.System,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance,
            new PassthroughToolCallReplayTreatment(),
            _magenticTurnRunner.Object);
    }

    private static ExecuteAgentTurnCommand CreateCommand(string agentName = "supervisor-agent") => new()
    {
        AgentName = agentName,
        UserMessage = "Plan and execute the task",
        TurnNumber = 1,
    };

    [Fact]
    public async Task Handle_AgentIsMagenticSupervisor_DelegatesToMagenticTurnRunner()
    {
        var supervisor = new AgentDefinition
        {
            Id = "supervisor-agent",
            Name = "Supervisor Agent",
            OrchestrationMode = AgentOrchestrationMode.Magentic,
            Participants = ["researcher"],
        };
        _agentRegistry.Setup(r => r.TryGet("supervisor-agent")).Returns(supervisor);

        _magenticTurnRunner
            .Setup(r => r.RunTurnAsync(supervisor, It.IsAny<string>(), "Plan and execute the task", It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = true,
                Response = "Synthesized answer",
                UpdatedHistory = [],
                InputTokens = 42,
                OutputTokens = 17,
            });

        var result = await CreateHandler().Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().Be("Synthesized answer");
        result.InputTokens.Should().Be(42);
        result.OutputTokens.Should().Be(17);

        _magenticTurnRunner.Verify(
            r => r.RunTurnAsync(supervisor, It.IsAny<string>(), "Plan and execute the task", It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The single-agent path must never run for a Magentic supervisor.
        _agentCache.Verify(
            c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_MagenticTurnFails_ReturnsFailureFromRunnerUnmodified()
    {
        var supervisor = new AgentDefinition
        {
            Id = "supervisor-agent",
            Name = "Supervisor Agent",
            OrchestrationMode = AgentOrchestrationMode.Magentic,
            Participants = ["researcher"],
        };
        _agentRegistry.Setup(r => r.TryGet("supervisor-agent")).Returns(supervisor);

        _magenticTurnRunner
            .Setup(r => r.RunTurnAsync(supervisor, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "magentic.round_limit_exceeded",
                ErrorKind = AgentTurnErrorKind.Internal,
            });

        var result = await CreateHandler().Handle(CreateCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("magentic.round_limit_exceeded");
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(0, false)]
    public async Task Handle_MagenticTurnFailsAfterSpending_WritesAFailedAssistantRowCarryingTheSpend(
        int inputTokens, bool expectRow)
    {
        // #780: a supervisor turn that fails after its participants ran has spent real money; it is
        // visible per message exactly as a single-agent failure is. One that spent nothing is not.
        var supervisor = new AgentDefinition
        {
            Id = "supervisor-agent",
            Name = "Supervisor Agent",
            OrchestrationMode = AgentOrchestrationMode.Magentic,
            Participants = ["researcher"],
        };
        _agentRegistry.Setup(r => r.TryGet("supervisor-agent")).Returns(supervisor);
        _magenticTurnRunner
            .Setup(r => r.RunTurnAsync(supervisor, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTurnResult
            {
                Success = false,
                Response = string.Empty,
                UpdatedHistory = [],
                Error = "magentic.round_limit_exceeded",
                ErrorKind = AgentTurnErrorKind.Internal,
                InputTokens = inputTokens,
                OutputTokens = inputTokens / 9,
                Model = "supervisor-model",
                ToolsInvoked = ["researcher_tool"],
            });
        var sessionId = Guid.NewGuid();
        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "supervisor-agent",
            UserMessage = "Plan and execute the task",
            TurnNumber = 3,
            ObservabilitySessionId = sessionId,
        };

        await CreateHandler().Handle(command, CancellationToken.None);

        _observability.Verify(
            o => o.RecordMessageAsync(
                sessionId, 3, "assistant", "assistant_failed", "magentic.round_limit_exceeded", "supervisor-model",
                inputTokens, inputTokens / 9, 0, 0, 0m, 0m,
                // The tools the failed turn invoked ride on its row, so the rollup's tool count is accounted for.
                It.Is<string[]?>(t => t != null && t.SequenceEqual(new[] { "researcher_tool" })),
                "magentic.round_limit_exceeded",
                It.Is<CancellationToken>(t => !t.CanBeCanceled)),
            expectRow ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Handle_AgentIsOrdinarySingleAgent_NeverCallsMagenticTurnRunner()
    {
        _agentRegistry.Setup(r => r.TryGet(It.IsAny<string>())).Returns((AgentDefinition?)null);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.Core.Tests.Helpers.TestableAIAgent("ok"));

        await CreateHandler().Handle(CreateCommand("ordinary-agent"), CancellationToken.None);

        _magenticTurnRunner.Verify(
            r => r.RunTurnAsync(
                It.IsAny<AgentDefinition>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_TurnDeadlineElapses_CancelsTheMagenticRun_InsteadOfLeavingItRunning()
    {
        // TimeoutBehavior throws to the caller at the deadline, but only a handler that observes its
        // ambient token actually stops. Without that, the supervisor and every participant keep making
        // model calls (and spending money) for a turn nobody is waiting on.
        var supervisor = new AgentDefinition
        {
            Id = "supervisor-agent",
            Name = "Supervisor Agent",
            OrchestrationMode = AgentOrchestrationMode.Magentic,
            Participants = ["researcher"],
        };
        _agentRegistry.Setup(r => r.TryGet("supervisor-agent")).Returns(supervisor);

        var runnerOutcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _magenticTurnRunner
            .Setup(r => r.RunTurnAsync(
                It.IsAny<AgentDefinition>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<MagenticTurnOverrides>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentDefinition _, string _, string _, IReadOnlyList<ChatMessage> _,
                MagenticTurnOverrides _, CancellationToken ct) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    runnerOutcome.TrySetResult("cancelled");
                    throw;
                }

                runnerOutcome.TrySetResult("ran to completion");
                return new AgentTurnResult { Success = true, Response = "late", UpdatedHistory = [] };
            });

        var handler = CreateHandler();
        var command = CreateCommand() with { Timeout = TimeSpan.FromMilliseconds(200) };
        var behavior = new TimeoutBehavior<ExecuteAgentTurnCommand, AgentTurnResult>(
            Mock.Of<IOptionsMonitor<AgentConfig>>(m => m.CurrentValue == new AgentConfig()),
            NullLogger<TimeoutBehavior<ExecuteAgentTurnCommand, AgentTurnResult>>.Instance);

        var act = () => behavior.Handle(command, () => handler.Handle(command, CancellationToken.None), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        (await runnerOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(
            "cancelled", "the deadline must stop the run, not just stop the caller waiting for it");
    }
}
