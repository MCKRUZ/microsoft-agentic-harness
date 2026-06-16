using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Models;
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
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Regression tests for the solution-review finding that the prerequisite middleware's
/// conversation scope silently fell back to a random <see cref="System.Guid"/> when the caller
/// failed to supply a conversation identifier. That fallback reset skill-completion state on every
/// agent rebuild and leaked unclearable tracker entries. The fix makes a missing conversation scope
/// a loud construction-time failure instead.
/// </summary>
public class AgentFactorySolutionReviewFixTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory;
    private readonly AgentFactory _factory;

    public AgentFactorySolutionReviewFixTests()
    {
        _chatClientFactory = new Mock<IChatClientFactory>();
        _chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FakeChatClient());

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
            null, null, null, null, null, null);

        _factory = new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            Mock.Of<IDistributedCache>(),
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            _chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }

    private static SkillPrerequisiteMap MapWithPrerequisites() => new()
    {
        Skills = new Dictionary<string, SkillPrerequisiteEntry>
        {
            ["validate"] = new SkillPrerequisiteEntry
            {
                SkillId = "validate",
                Prerequisites = [],
                CompletionTool = "run_validation",
                ToolNames = ["run_validation"]
            },
            ["deploy"] = new SkillPrerequisiteEntry
            {
                SkillId = "deploy",
                Prerequisites = ["validate"],
                ToolNames = ["apply_deployment"]
            }
        }
    };

    private static AgentExecutionContext ContextWithPrerequisites(
        IReadOnlyDictionary<string, object>? extraProperties = null)
    {
        var props = new Dictionary<string, object>
        {
            [SkillPrerequisiteMap.AdditionalPropertiesKey] = MapWithPrerequisites()
        };

        if (extraProperties is not null)
        {
            foreach (var (key, value) in extraProperties)
                props[key] = value;
        }

        return new AgentExecutionContext
        {
            Name = "prereq-agent",
            Instruction = "test",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = props
        };
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesButNoConversationScope_ThrowsInsteadOfSilentGuidFallback()
    {
        // Arrange — prerequisite map present, but caller supplied no conversationId.
        // Old behavior: a throwaway Guid scope was minted and the agent was built successfully.
        var context = ContextWithPrerequisites();

        // Act
        var act = () => _factory.CreateAgentAsync(context);

        // Assert — the missing wiring is now surfaced loudly.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*conversationId*");
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesWithWhitespaceConversationScope_Throws()
    {
        // Arrange — a present-but-blank scope is as broken as a missing one.
        var context = ContextWithPrerequisites(new Dictionary<string, object>
        {
            [AgentFactory.ConversationIdPropertyKey] = "   "
        });

        // Act
        var act = () => _factory.CreateAgentAsync(context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*conversationId*");
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesWithConversationScope_Succeeds()
    {
        // Arrange — caller correctly flows the conversation identifier through.
        var context = ContextWithPrerequisites(new Dictionary<string, object>
        {
            [AgentFactory.ConversationIdPropertyKey] = "conversation-123"
        });

        // Act
        var agent = await _factory.CreateAgentAsync(context);

        // Assert — wiring is correct, so the agent builds normally.
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAgentAsync_NoPrerequisites_DoesNotRequireConversationScope()
    {
        // Arrange — without prerequisites the middleware is never wired, so no scope is needed.
        var context = new AgentExecutionContext
        {
            Name = "plain-agent",
            Instruction = "test",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
        };

        // Act
        var agent = await _factory.CreateAgentAsync(context);

        // Assert
        agent.Should().NotBeNull();
    }
}
