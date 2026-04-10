using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Services.Agent;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.Tests.Helpers;
using Domain.AI.Skills;
using FluentAssertions;
using MediatR;
using Microsoft.Agents.AI;
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
    private static ServiceProvider BuildPipeline(Action<Mock<IAgentFactory>> configureFactory)
    {
        var factoryMock = new Mock<IAgentFactory>();
        configureFactory(factoryMock);

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

        // Agent factory — mock returns testable agents
        services.AddSingleton(factoryMock.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RunConversation_SingleTurn_CompletesWithoutDoubleInitError()
    {
        // Arrange
        using var provider = BuildPipeline(factory =>
            factory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
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
        // Arrange
        var turnResponses = new[] { "Turn 1 response", "Turn 2 response", "Turn 3 response" };
        var callIndex = 0;

        using var provider = BuildPipeline(factory =>
            factory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TestableAIAgent(turnResponses[callIndex++])));

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
        using var provider = BuildPipeline(factory =>
            factory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
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
        result.Error.Should().Contain("Model unavailable");
    }

    [Fact]
    public async Task ExecuteAgentTurn_Standalone_SetsAgentContext()
    {
        // Arrange
        using var provider = BuildPipeline(factory =>
            factory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(), It.IsAny<SkillAgentOptions>(), It.IsAny<CancellationToken>()))
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
