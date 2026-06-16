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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Regression tests for prerequisite-scope resolution in <see cref="AgentFactory"/>
/// (solution review finding idx 22 / cluster 10).
/// </summary>
/// <remarks>
/// Before the fix, a prerequisite-bearing agent built without a <c>conversationId</c> in
/// <see cref="AgentExecutionContext.AdditionalProperties"/> silently minted a throwaway
/// <see cref="System.Guid"/> scope. That fallback was always taken in production (no caller set
/// the key), which (1) reset all skill-completion state whenever the cached agent was rebuilt and
/// (2) leaked tracker entries that no eviction path could clear. The fix removes the silent
/// fallback: the factory now honors a supplied conversation id under
/// <see cref="AgentFactory.ConversationIdPropertyKey"/> and fails loudly when it is absent, matching
/// how the factory already rejects every other construction-time misconfiguration.
/// </remarks>
public class AgentFactoryPrerequisiteScopeSolutionReviewFixTests
{
    private readonly AgentFactory _factory;

    public AgentFactoryPrerequisiteScopeSolutionReviewFixTests()
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
            chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesWithoutConversationId_ThrowsInsteadOfSilentFallback()
    {
        // Arrange — prerequisite-bearing context with NO conversation scope supplied.
        // Old behavior: silently used Guid.NewGuid(). New behavior: fail loudly.
        var context = BuildPrerequisiteContext(conversationId: null);

        // Act
        var act = () => _factory.CreateAgentAsync(context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{AgentFactory.ConversationIdPropertyKey}*");
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesWithBlankConversationId_Throws()
    {
        // Arrange — an empty/whitespace string is not a usable conversation scope.
        var context = BuildPrerequisiteContext(conversationId: "   ");

        // Act
        var act = () => _factory.CreateAgentAsync(context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAgentAsync_PrerequisitesWithConversationId_SucceedsAndHonorsScope()
    {
        // Arrange — caller flowed the real conversation id under the documented key.
        var context = BuildPrerequisiteContext(conversationId: "conv-123");

        // Act
        var agent = await _factory.CreateAgentAsync(context);

        // Assert — supplied scope is accepted, so construction completes without throwing.
        agent.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAgentAsync_NoPrerequisites_DoesNotRequireConversationId()
    {
        // Arrange — without prerequisites the middleware is not wired, so no scope is needed.
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

    private static AgentExecutionContext BuildPrerequisiteContext(string? conversationId)
    {
        var prereqMap = new SkillPrerequisiteMap
        {
            Skills = new Dictionary<string, SkillPrerequisiteEntry>
            {
                ["deploy"] = new SkillPrerequisiteEntry
                {
                    SkillId = "deploy",
                    Prerequisites = ["validate"],
                    ToolNames = ["run_deploy"]
                }
            }
        };

        var props = new Dictionary<string, object>
        {
            [SkillPrerequisiteMap.AdditionalPropertiesKey] = prereqMap
        };
        if (conversationId is not null)
            props[AgentFactory.ConversationIdPropertyKey] = conversationId;

        return new AgentExecutionContext
        {
            Name = "prereq-agent",
            Instruction = "test",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = props
        };
    }
}
