using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// The chat response cache an agent is built with gives every cached response an expiry (#901). Before this, the caching
/// client wrote entries with none, and a Redis-backed cache kept every model response for good.
/// </summary>
public sealed class AgentFactoryResponseCacheExpiryTests
{
    private readonly List<DistributedCacheEntryOptions> _writes = [];
    private readonly AppConfig _config = new()
    {
        AI = new AIConfig
        {
            AgentFramework = new AgentFrameworkConfig
            {
                DefaultDeployment = "gpt-4o",
                ClientType = AIAgentFrameworkClientType.AzureOpenAI,
            },
        },
    };

    [Fact]
    public async Task ACachedResponse_ExpiresWithinTheConfiguredLifetime()
    {
        _config.Cache.ChatResponseExpiry = TimeSpan.FromMinutes(20);
        var before = DateTimeOffset.UtcNow;

        var agent = await CreateFactory().CreateAgentAsync(new AgentExecutionContext
        {
            Name = "agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
        });
        await agent.RunAsync("hello");

        _writes.Should().NotBeEmpty("the response must have been cached for the expiry to be checked");
        _writes.Should().OnlyContain(o => o.AbsoluteExpiration.HasValue
            && o.AbsoluteExpiration.Value <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)
            && o.AbsoluteExpiration.Value >= before + TimeSpan.FromMinutes(19));
    }

    [Fact]
    public void AHugeConfiguredExpiry_IsCappedAtThirtyDays()
    {
        _config.Cache.ChatResponseExpiry = TimeSpan.MaxValue;

        CreateFactory().CurrentRetentionPeriod.Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public void TheReceipt_IsToldTheLivePeriod()
    {
        _config.Cache.ChatResponseExpiry = TimeSpan.FromMinutes(20);

        CreateFactory().CurrentRetentionPeriod.Should().Be(TimeSpan.FromMinutes(20));
    }

    private AgentFactory CreateFactory()
    {
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _config);
        var chatClients = new Mock<IChatClientFactory>();
        chatClients.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(true);
        chatClients.Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient());

        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, _, options, _) => _writes.Add(options))
            .Returns(Task.CompletedTask);

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, new UnsandboxedSkillFileReader(), null!,
            Mock.Of<IAgentMetadataRegistry>(r => r.GetAll() == new List<AgentDefinition>()),
            null!, null!, null!, null!);

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            cache.Object,
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClients.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }
}
