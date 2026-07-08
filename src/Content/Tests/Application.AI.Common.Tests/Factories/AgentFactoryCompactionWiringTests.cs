using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Compaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Proves the context-compaction subsystem is wired to the live agent turn path. Before this
/// wiring, <see cref="IContextCompactionService"/> was DI-registered but had zero consumers, so
/// conversation history was never compacted on any turn. These tests use a canary
/// <see cref="FakeChatClient"/> and a spy <see cref="IContextCompactionService"/> to assert that
/// when compaction is enabled in config, live turns invoke the service and the trimmed (summary)
/// history reaches the model; and that when compaction is disabled (the default) the full history
/// passes through untouched.
/// </summary>
public sealed class AgentFactoryCompactionWiringTests
{
    private const string SummaryMarker = "COMPACTED_SUMMARY";

    private static AgentFactory CreateFactory(
        FakeChatClient innerClient,
        bool compactionEnabled,
        IContextCompactionService compactionService)
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
            .ReturnsAsync(innerClient);

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                },
                ContextManagement = new ContextManagementConfig
                {
                    Compaction = new CompactionConfig
                    {
                        MiddlewareEnabled = compactionEnabled,
                        MiddlewareMaxContextTokens = 1000
                    }
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

        var services = new ServiceCollection();
        services.AddSingleton(compactionService);
        var serviceProvider = services.BuildServiceProvider();

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClientFactory.Object,
            serviceProvider,
            new InMemorySkillCompletionTracker());
    }

    private static CompactionResult SuccessfulCompaction() =>
        CompactionResult.Succeeded(new CompactionBoundaryMessage
        {
            Id = "boundary-1",
            Trigger = CompactionTrigger.Manual,
            Strategy = CompactionStrategy.Full,
            PreCompactionTokens = 5000,
            PostCompactionTokens = 50,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = SummaryMarker
        });

    private static AgentExecutionContext AgentContext() => new()
    {
        Name = "compaction-canary-agent",
        Instruction = "You are a test agent.",
        AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
    };

    [Fact]
    public async Task CreateAgentAsync_CompactionEnabledAndBudgetExceeded_TurnRoutesThroughCompaction()
    {
        var innerClient = new FakeChatClient().WithDefaultResponse("ok");
        var service = new Mock<IContextCompactionService>();
        service
            .Setup(s => s.ShouldAutoCompact(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);
        service
            .Setup(s => s.CompactAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<CompactionStrategy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessfulCompaction());

        var factory = CreateFactory(innerClient, compactionEnabled: true, service.Object);

        var agent = await factory.CreateAgentAsync(AgentContext());
        await agent.RunAsync("please answer this");

        // The compaction service must have been consulted and asked to compact on the live turn.
        service.Verify(
            s => s.CompactAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                CompactionStrategy.Full,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "an enabled compaction middleware must invoke the service when the budget is exceeded");

        // The trimmed history (summary system message) must be what the model actually receives.
        innerClient.RequestHistory.Should().NotBeEmpty();
        innerClient.RequestHistory[^1]
            .Should().Contain(m => m.Text == SummaryMarker,
                "the compacted summary must replace the prior history before the model call");
    }

    [Fact]
    public async Task CreateAgentAsync_CompactionDisabled_FullHistoryPassesThroughUntouched()
    {
        var innerClient = new FakeChatClient().WithDefaultResponse("ok");
        var service = new Mock<IContextCompactionService>(MockBehavior.Strict);

        var factory = CreateFactory(innerClient, compactionEnabled: false, service.Object);

        var agent = await factory.CreateAgentAsync(AgentContext());
        await agent.RunAsync("please answer this");

        // Default (disabled) behavior: the compaction service is never touched.
        service.Verify(
            s => s.CompactAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<CompactionStrategy>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        innerClient.RequestHistory.Should().NotBeEmpty();
        innerClient.RequestHistory[^1]
            .Should().NotContain(m => m.Text == SummaryMarker);
    }

    [Fact]
    public async Task CreateAgentAsync_CompactionFails_FailsOpenAndForwardsOriginalHistory()
    {
        var innerClient = new FakeChatClient().WithDefaultResponse("ok");
        var service = new Mock<IContextCompactionService>();
        service
            .Setup(s => s.ShouldAutoCompact(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);
        service
            .Setup(s => s.CompactAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<CompactionStrategy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompactionResult.Failed("summarizer unavailable"));

        var factory = CreateFactory(innerClient, compactionEnabled: true, service.Object);

        var agent = await factory.CreateAgentAsync(AgentContext());
        var response = await agent.RunAsync("please answer this");

        // Compaction failure must not break the turn, and must not inject a phantom summary.
        response.Should().NotBeNull();
        innerClient.RequestHistory.Should().NotBeEmpty();
        innerClient.RequestHistory[^1]
            .Should().NotContain(m => m.Text == SummaryMarker,
                "a failed compaction forwards the original history rather than a summary");
    }
}
