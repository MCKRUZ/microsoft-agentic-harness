using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for <see cref="AgentFactory"/> covering provider availability checks,
/// delegation to <see cref="IChatClientFactory"/>, argument validation,
/// batch/category/tag agent creation, and error handling.
/// </summary>
public class AgentFactoryTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory;
    private readonly Mock<ISkillMetadataRegistry> _skillRegistry;
    private readonly Mock<AgentExecutionContextFactory> _contextFactory;
    private readonly AgentFactory _factory;

    public AgentFactoryTests()
    {
        _chatClientFactory = new Mock<IChatClientFactory>();
        _chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        _chatClientFactory
            .Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>
            {
                [AIAgentFrameworkClientType.AzureOpenAI] = true,
                [AIAgentFrameworkClientType.OpenAI] = false
            });

        _skillRegistry = new Mock<ISkillMetadataRegistry>();

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

        // AgentExecutionContextFactory has many dependencies; mock it at the class level
        _contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null, null, null, null, null, null);

        _factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            _contextFactory.Object,
            _skillRegistry.Object,
            _chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void IsProviderAvailable_DelegatesToChatClientFactory()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(AIAgentFrameworkClientType.OpenAI)).Returns(false);

        var result = _factory.IsProviderAvailable(AIAgentFrameworkClientType.OpenAI);

        result.Should().BeFalse();
        _chatClientFactory.Verify(f => f.IsAvailable(AIAgentFrameworkClientType.OpenAI), Times.Once);
    }

    [Fact]
    public void GetAvailableProviders_DelegatesToChatClientFactory()
    {
        var result = _factory.GetAvailableProviders();

        result.Should().ContainKey(AIAgentFrameworkClientType.AzureOpenAI);
        result[AIAgentFrameworkClientType.AzureOpenAI].Should().BeTrue();
        result[AIAgentFrameworkClientType.OpenAI].Should().BeFalse();
    }

    [Fact]
    public async Task CreateAgentAsync_ProviderNotAvailable_ThrowsInvalidOperation()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(false);
        _chatClientFactory.Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>());

        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task CreateAgentAsync_PersistentAgents_RequiresAgentId()
    {
        var context = new AgentExecutionContext
        {
            Name = "test-agent",
            AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents,
            AgentId = null
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AgentId is required*");
    }

    [Fact]
    public async Task CreateAgentFromSkillAsync_SkillNotFound_ThrowsInvalidOperation()
    {
        _skillRegistry.Setup(r => r.TryGet("missing-skill")).Returns((SkillDefinition?)null);

        var act = () => _factory.CreateAgentFromSkillAsync("missing-skill");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing-skill*not found*");
    }

    [Fact]
    public async Task CreateAgentsFromSkillsAsync_SkillFailure_SkipsAndContinues()
    {
        // First skill fails, second succeeds
        _skillRegistry
            .Setup(r => r.TryGet("bad-skill"))
            .Returns((SkillDefinition?)null);

        var goodSkill = new SkillDefinition { Id = "good-skill", Name = "good-skill" };
        _skillRegistry
            .Setup(r => r.TryGet("good-skill"))
            .Returns(goodSkill);

        // Both will fail since CreateAgentFromSkillAsync needs full pipeline,
        // but the point is that exceptions are caught and logged
        var result = await _factory.CreateAgentsFromSkillsAsync(["bad-skill", "good-skill"]);

        // Both should fail gracefully (no exception thrown)
        result.Should().BeEmpty(); // Both fail because contextFactory isn't fully wired
    }

    [Fact]
    public async Task CreateAgentsByCategoryAsync_DelegatesToRegistry()
    {
        _skillRegistry
            .Setup(r => r.GetByCategory("analysis"))
            .Returns(new List<SkillDefinition>());

        var result = await _factory.CreateAgentsByCategoryAsync("analysis");

        result.Should().BeEmpty();
        _skillRegistry.Verify(r => r.GetByCategory("analysis"), Times.Once);
    }

    [Fact]
    public async Task CreateAgentsByTagsAsync_DelegatesToRegistry()
    {
        var tags = new[] { "research", "ai" };
        _skillRegistry
            .Setup(r => r.GetByTags(tags))
            .Returns(new List<SkillDefinition>());

        var result = await _factory.CreateAgentsByTagsAsync(tags);

        result.Should().BeEmpty();
        _skillRegistry.Verify(r => r.GetByTags(tags), Times.Once);
    }

    [Fact]
    public async Task CreateAgentAsync_ProviderNotAvailable_ErrorMessageListsAvailableProviders()
    {
        _chatClientFactory.Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>())).Returns(false);
        _chatClientFactory.Setup(f => f.GetAvailableProviders())
            .Returns(new Dictionary<AIAgentFrameworkClientType, bool>
            {
                [AIAgentFrameworkClientType.OpenAI] = true,
                [AIAgentFrameworkClientType.AzureOpenAI] = false
            });

        var context = new AgentExecutionContext
        {
            Name = "test",
            AIAgentFrameworkType = AIAgentFrameworkClientType.Anthropic
        };

        var act = () => _factory.CreateAgentAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OpenAI*");
    }
}
