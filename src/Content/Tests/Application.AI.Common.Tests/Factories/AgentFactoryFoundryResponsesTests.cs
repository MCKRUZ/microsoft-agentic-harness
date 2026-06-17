using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for the <see cref="AIAgentFrameworkClientType.FoundryResponses"/> branch of
/// <see cref="AgentFactory.CreateAgentAsync"/>: delegation to <see cref="IFoundryAgentProvider"/>,
/// injection of the harness middleware pipeline through the provider's client-factory hook, and the
/// fail-fast error when the provider is not registered.
/// </summary>
public class AgentFactoryFoundryResponsesTests
{
    private static AgentFactory BuildFactory(IServiceProvider serviceProvider)
    {
        var chatClientFactory = new Mock<IChatClientFactory>();
        chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        // FoundryResponses must never reach IChatClientFactory.GetChatClientAsync — the factory
        // branches to IFoundryAgentProvider first. Make a call here fail the test loudly.
        chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                AIAgentFrameworkClientType.FoundryResponses,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "GetChatClientAsync must not be called for FoundryResponses."));

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.FoundryResponses
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null, null, null, null, null, null);

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            contextFactory.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            chatClientFactory.Object,
            serviceProvider,
            new InMemorySkillCompletionTracker());
    }

    private static IServiceProvider ProviderWith(IFoundryAgentProvider foundryProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(foundryProvider);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CreateAgentAsync_FoundryResponses_DelegatesToFoundryProviderWithResolvedModel()
    {
        var fake = new FakeFoundryAgentProvider();
        var factory = BuildFactory(ProviderWith(fake));

        var context = new AgentExecutionContext
        {
            Name = "foundry-agent",
            Instruction = "be helpful",
            DeploymentName = "gpt-4o-mini",
            AIAgentFrameworkType = AIAgentFrameworkClientType.FoundryResponses
        };

        var agent = await factory.CreateAgentAsync(context);

        agent.Should().NotBeNull();
        fake.CallCount.Should().Be(1);
        fake.CapturedModel.Should().Be("gpt-4o-mini");
        fake.CapturedOptions!.Name.Should().Be("foundry-agent");
        fake.CapturedOptions!.ChatOptions!.Instructions.Should().Be("be helpful");
    }

    [Fact]
    public async Task CreateAgentAsync_FoundryResponses_FallsBackToDefaultDeployment()
    {
        var fake = new FakeFoundryAgentProvider();
        var factory = BuildFactory(ProviderWith(fake));

        var context = new AgentExecutionContext
        {
            Name = "foundry-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.FoundryResponses
        };

        await factory.CreateAgentAsync(context);

        // No DeploymentName on the context → AgentFramework.DefaultDeployment ("gpt-4o").
        fake.CapturedModel.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task CreateAgentAsync_FoundryResponses_InjectsMiddlewarePipelineViaClientFactory()
    {
        var fake = new FakeFoundryAgentProvider();
        var factory = BuildFactory(ProviderWith(fake));

        var context = new AgentExecutionContext
        {
            Name = "foundry-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.FoundryResponses
        };

        await factory.CreateAgentAsync(context);

        // The factory must hand the provider a client-factory that wraps the inner Responses client
        // in the harness middleware pipeline — invoking it should produce a decorated client.
        fake.CapturedClientFactory.Should().NotBeNull();
        var inner = new FakeChatClient();
        var wrapped = fake.CapturedClientFactory!(inner);
        wrapped.Should().NotBeSameAs(inner);
    }

    [Fact]
    public async Task CreateAgentAsync_FoundryResponses_ProviderNotRegistered_ThrowsInvalidOperation()
    {
        // Empty service provider — no IFoundryAgentProvider (AIFoundry not configured).
        var factory = BuildFactory(new ServiceCollection().BuildServiceProvider());

        var context = new AgentExecutionContext
        {
            Name = "foundry-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.FoundryResponses
        };

        var act = () => factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IFoundryAgentProvider*");
    }

    /// <summary>Captures the arguments the factory passes so the delegation can be asserted.</summary>
    private sealed class FakeFoundryAgentProvider : IFoundryAgentProvider
    {
        public int CallCount { get; private set; }
        public string? CapturedModel { get; private set; }
        public ChatClientAgentOptions? CapturedOptions { get; private set; }
        public Func<IChatClient, IChatClient>? CapturedClientFactory { get; private set; }

        public Task<AIAgent> CreateAgentAsync(
            string model,
            ChatClientAgentOptions options,
            Func<IChatClient, IChatClient> clientFactory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CapturedModel = model;
            CapturedOptions = options;
            CapturedClientFactory = clientFactory;
            AIAgent agent = new ChatClientAgent(new FakeChatClient(), options);
            return Task.FromResult(agent);
        }
    }
}
