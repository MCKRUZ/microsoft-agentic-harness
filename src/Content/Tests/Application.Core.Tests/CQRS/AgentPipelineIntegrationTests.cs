using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Notifications;
using Application.AI.Common.Services.Agent;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.Tests.Helpers;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services.AI;
using Domain.AI.Skills;
using FluentAssertions;
using MediatR;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Integration tests that wire up real MediatR pipeline with real behaviors
/// to catch runtime issues (like double-init of scoped context) that unit
/// tests with mocked IMediator cannot detect.
/// </summary>
public class AgentPipelineIntegrationTests
{
    private static ServiceProvider BuildPipeline(Action<Mock<IAgentConversationCache>> configureCache)
    {
        var cacheMock = new Mock<IAgentConversationCache>();
        configureCache(cacheMock);

        var services = new ServiceCollection();

        // Logging — NullLogger for all
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // MediatR — scan Application.Core for handlers (RunConversation, ExecuteAgentTurn)
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RunConversationCommandHandler).Assembly));

        // Agent context propagation — the behavior that caused the double-init bug
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>));

        // Scoped agent execution context — real implementation, not mock
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // Tool-invocation governor — not under test here; permissive mock so the handler resolves.
        var governorMock = new Mock<Application.AI.Common.Interfaces.Governance.IToolInvocationGovernor>();
        governorMock
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application.AI.Common.Interfaces.Governance.ToolInvocationDecision.Allow());
        services.AddScoped(_ => governorMock.Object);

        // Progress / spin guard — not under test here; permissive mock so the handler resolves.
        var progressMock = new Mock<Application.AI.Common.Interfaces.Governance.IProgressEvaluator>();
        progressMock
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(Application.AI.Common.Interfaces.Governance.ProgressVerdict.Continue());
        services.AddScoped(_ => progressMock.Object);

        // Classification DLP gate — not under test here; permissive mock so the handler resolves.
        var classificationMock = new Mock<Application.AI.Common.Interfaces.Governance.IToolClassificationGate>();
        classificationMock
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Application.AI.Common.Interfaces.Governance.ClassificationVerdict.Allow());
        services.AddScoped(_ => classificationMock.Object);

        // Consumer tool-call observers — none registered here, matching the default composition, so
        // the chain reports itself empty and the chokepoint skips it.
        var observerChainMock = new Mock<Application.AI.Common.Interfaces.Governance.IToolCallObserverChain>();
        observerChainMock.SetupGet(c => c.HasObservers).Returns(false);
        services.AddScoped(_ => observerChainMock.Object);

        // The REAL admission chain over the four permissive gates above, not a mock of it. The handler
        // depends only on the chain now, and registering the real one keeps this an end-to-end proof
        // that the four gates are reachable from the turn — a mocked chain would prove nothing about
        // whether they are wired at all.
        services.AddScoped<
            Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline,
            Application.AI.Common.Services.Governance.ToolCallAdmissionPipeline>();

        // Conversation-lifetime budget — not under test here; permissive mock (disabled) so the
        // RunConversation handler resolves and never reports exhaustion.
        var budgetMock = new Mock<Application.AI.Common.Interfaces.AI.IConversationBudgetTracker>();
        budgetMock
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Domain.AI.Budget.ConversationBudgetStatus.Disabled);
        services.AddSingleton(budgetMock.Object);

        // Agent conversation cache — mock returns testable agents
        services.AddSingleton(cacheMock.Object);

        // Agent metadata registry — no manifests configured; TryGet returns null so the
        // handler falls back to using AgentName as the skill id (matches the mock above).
        var registryMock = new Mock<IAgentMetadataRegistry>();
        registryMock.Setup(r => r.TryGet(It.IsAny<string>())).Returns((Domain.AI.Agents.AgentDefinition?)null);
        services.AddSingleton(registryMock.Object);

        // Observability store — no-op mock for integration tests
        services.AddSingleton(new Mock<IObservabilityStore>().Object);

        // The real shared telemetry recorder, resolved from the container like everything else. A
        // self-contained run reads and writes no conversation record, so the strict store registered
        // below stays untouched through it — which is the same guard, one layer along.
        services.AddSingleton<IConversationTelemetryRecorder, ConversationTelemetryRecorder>();

        // Durable-conversation collaborators. Strict and unstubbed on purpose: every conversation this
        // pipeline runs is self-contained (no ConversationOwnerId), so a call to either would mean the
        // handler had taken the durable path. They are registered rather than omitted because the
        // handler demands them as ordinary constructor dependencies — which is what makes a host that
        // forgets to compose conversation storage fail at startup instead of on its first durable run.
        services.AddSingleton(
            new Mock<Application.AI.Common.Interfaces.AI.IConversationStore>(MockBehavior.Strict).Object);
        services.AddSingleton(
            new Mock<Application.AI.Common.Interfaces.AI.IConversationTurnLease>(MockBehavior.Strict).Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new Domain.Common.Config.AI.Conversations.ConversationsConfig()));

        // LLM usage capture — scoped mock matching production DI lifetime
        var usageCaptureMock = new Mock<ILlmUsageCapture>();
        usageCaptureMock.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>()));
        services.AddScoped<ILlmUsageCapture>(_ => usageCaptureMock.Object);

        // Skill registry + registration tracker — required by the handler's per-turn
        // context snapshot builder. Skill registry mocked (no skills resolve), matching
        // the "no manifests" stance of the rest of this pipeline.
        var skillRegistryMock = new Mock<ISkillMetadataRegistry>();
        services.AddSingleton(skillRegistryMock.Object);
        services.AddSingleton<
            Application.AI.Common.Interfaces.Context.IConversationRegistrationTracker,
            Application.AI.Common.Services.Context.ConversationRegistrationTracker>();

        // Foresight context-snapshot pipeline — pure computer + no-op notifier
        // so the handler's snapshot hook resolves cleanly in this integration test.
        services.AddSingleton<IContextSnapshotComputer, DefaultContextSnapshotComputer>();
        services.AddSingleton<IContextSnapshotNotifier, NullContextSnapshotNotifier>();
        services.AddSingleton(TimeProvider.System);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RunConversation_SingleTurn_CompletesWithoutDoubleInitError()
    {
        // Arrange
        using var provider = BuildPipeline(cache =>
            cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TestableAIAgent("Hello from the agent")));

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "research-agent",
            UserMessages = ["Do some research on GaussianSplatting"]
        };

        // Act — this threw InvalidOperationException before the fix
        var result = await mediator.Send(command);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().ContainSingle();
        result.FinalResponse.Should().Contain("Hello from the agent");
    }

    [Fact]
    public async Task RunConversation_MultiTurn_EachTurnSetsContextCorrectly()
    {
        // Arrange — single cached agent returns a different response each RunAsync call
        var turnResponses = new[] { "Turn 1 response", "Turn 2 response", "Turn 3 response" };
        var callIndex = 0;
        var agent = new TestableAIAgent((_, _) =>
            Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, turnResponses[callIndex++]))));

        using var provider = BuildPipeline(cache =>
            cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(agent));

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "multi-turn-agent",
            UserMessages = ["Question 1", "Question 2", "Question 3"]
        };

        // Act
        var result = await mediator.Send(command);

        // Assert
        result.Success.Should().BeTrue();
        result.Turns.Should().HaveCount(3);
        result.Turns[0].AgentResponse.Should().Contain("Turn 1");
        result.Turns[1].AgentResponse.Should().Contain("Turn 2");
        result.Turns[2].AgentResponse.Should().Contain("Turn 3");
    }

    [Fact]
    public async Task RunConversation_AgentThrows_ReturnsFailureGracefully()
    {
        // Arrange
        using var provider = BuildPipeline(cache =>
            cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TestableAIAgent.Throwing(new InvalidOperationException("Model unavailable"))));

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "failing-agent",
            UserMessages = ["Hello"]
        };

        // Act
        var result = await mediator.Send(command);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("An internal error occurred during the agent turn.");
    }

    [Fact]
    public async Task ExecuteAgentTurn_Standalone_SetsAgentContext()
    {
        // Arrange
        using var provider = BuildPipeline(cache =>
            cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TestableAIAgent("Direct turn response")));

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var context = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = "test-agent",
            UserMessage = "Hello",
            ConversationId = "test-conv-42",
            TurnNumber = 5
        };

        // Act
        var result = await mediator.Send(command);

        // Assert
        result.Success.Should().BeTrue();
        context.AgentId.Should().Be("test-agent");
        context.ConversationId.Should().Be("test-conv-42");
        context.TurnNumber.Should().Be(5);
    }
}
