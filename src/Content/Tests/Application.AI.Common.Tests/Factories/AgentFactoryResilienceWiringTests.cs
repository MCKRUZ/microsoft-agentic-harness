using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Proves the resilience subsystem is wired to the live agent path (audit item F3).
/// Pre-fix, <see cref="AgentExecutionContextFactory"/> stashed the composed resilient chat
/// client into <c>AdditionalProperties["__resilientChatClient"]</c> but nothing ever read it —
/// <see cref="AgentFactory"/> always built agents on the raw <see cref="IChatClientFactory"/>
/// client, so the Polly retry/circuit-breaker/fallback pipelines never executed on any turn.
/// These tests use a canary <see cref="FakeChatClient"/> as the "resilient" client and assert
/// that live turns actually route through it when resilience is enabled, and that the raw
/// per-context client is used (byte-identical legacy behavior) when resilience is disabled.
/// </summary>
public sealed class AgentFactoryResilienceWiringTests
{
    /// <summary>
    /// Key AgentExecutionContextFactory uses to stash the resilient client. Bound to the
    /// production constant so the producer/consumer contract cannot silently drift.
    /// </summary>
    private const string ResilientClientKey = IResilientChatClientProvider.AdditionalPropertiesKey;

    // ---------------------------------------------------------------------
    // AgentFactory: consumption of the stashed resilient client
    // ---------------------------------------------------------------------

    private static AgentFactory CreateAgentFactory(FakeChatClient rawClient)
    {
        var chatClientFactory = new Mock<IChatClientFactory>();
        chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawClient);

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, null!, null!, null!, null!);

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }

    [Fact]
    public async Task CreateAgentAsync_ResilientClientInContext_TurnsRouteThroughResilientClient()
    {
        // The canary records every request it receives. Pre-fix it records ZERO because the
        // stashed resilient client was never consumed — the raw factory client took the call.
        var resilientCanary = new FakeChatClient().WithDefaultResponse("from-resilient-chain");
        var rawClient = new FakeChatClient().WithDefaultResponse("from-raw-client");
        var factory = CreateAgentFactory(rawClient);

        var context = new AgentExecutionContext
        {
            Name = "resilience-canary-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>
            {
                [ResilientClientKey] = resilientCanary
            }
        };

        var agent = await factory.CreateAgentAsync(context);
        var response = await agent.RunAsync("hello");

        resilientCanary.RequestHistory.Should().NotBeEmpty(
            "when resilience is enabled the composed fallback-chain client must carry live turns");
        rawClient.RequestHistory.Should().BeEmpty(
            "the raw per-provider client must not bypass the resilience pipelines");
        response.Text.Should().Contain("from-resilient-chain");
    }

    [Fact]
    public async Task CreateAgentAsync_NoResilientClientInContext_TurnsRouteThroughFactoryClient()
    {
        // Resilience disabled (nothing stashed) => shipped default behavior is unchanged:
        // the per-context raw client from IChatClientFactory carries the turn.
        var rawClient = new FakeChatClient().WithDefaultResponse("from-raw-client");
        var factory = CreateAgentFactory(rawClient);

        var context = new AgentExecutionContext
        {
            Name = "plain-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>()
        };

        var agent = await factory.CreateAgentAsync(context);
        var response = await agent.RunAsync("hello");

        rawClient.RequestHistory.Should().NotBeEmpty();
        response.Text.Should().Contain("from-raw-client");
    }

    // ---------------------------------------------------------------------
    // AgentExecutionContextFactory: config-gated stash
    // ---------------------------------------------------------------------

    private static AgentExecutionContextFactory CreateContextFactory(
        bool resilienceEnabled,
        IResilientChatClientProvider resilientProvider)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI,
                    DefaultDeployment = "gpt-4o"
                },
                Resilience = new ResilienceConfig { Enabled = resilienceEnabled }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var services = new ServiceCollection().BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, services),
            new SkillPrerequisiteResolver(),
            resilientChatClientProvider: resilientProvider);
    }

    private static SkillDefinition SimpleSkill() => new()
    {
        Id = "test-skill",
        Name = "test-skill",
        Instructions = "You are a test agent."
    };

    [Fact]
    public async Task MapToAgentContextAsync_ResilienceDisabled_DoesNotStashResilientClient()
    {
        // When ResilienceConfig.Enabled=false the provider would return the PRIMARY raw client
        // (AppConfig.AI.AgentFramework), which must not override per-skill deployment/framework
        // overrides on the context. The stash must therefore be config-gated.
        var provider = new Mock<IResilientChatClientProvider>(MockBehavior.Strict);
        var factory = CreateContextFactory(resilienceEnabled: false, provider.Object);

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.AdditionalProperties.Should().NotContainKey(ResilientClientKey,
            "resilience is disabled, so the raw per-context client path must remain untouched");
        provider.Verify(
            p => p.GetResilientChatClientAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "the fallback chain must not even be composed when resilience is off");
    }

    [Fact]
    public async Task MapToAgentContextAsync_ResilienceEnabled_StashesResilientClient()
    {
        var canary = new FakeChatClient();
        var provider = new Mock<IResilientChatClientProvider>();
        provider
            .Setup(p => p.GetResilientChatClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(canary);
        var factory = CreateContextFactory(resilienceEnabled: true, provider.Object);

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey(ResilientClientKey);
        context.AdditionalProperties![ResilientClientKey].Should().BeSameAs(canary,
            "the exact composed resilient client must flow to AgentFactory");
    }
}
