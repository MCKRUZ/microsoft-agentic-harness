using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

public class AgentExecutionContextFactoryTests
{
    private static AgentExecutionContextFactory CreateFactory(
        AIAgentFrameworkClientType configuredClientType = AIAgentFrameworkClientType.AzureOpenAI,
        string? deployment = "default-model")
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = configuredClientType,
                    DefaultDeployment = deployment,
                    ApiKey = "test-key",
                    Endpoint = "https://test.example.com"
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var services = new ServiceCollection().BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services,
            NullLoggerFactory.Instance,
            toolConverter: null,
            mcpToolProvider: null,
            budgetTracker: null);
    }

    private static SkillDefinition SimpleSkill(string id = "test-skill") => new()
    {
        Id = id,
        Name = id,
        Instructions = "You are a test agent."
    };

    [Fact]
    public async Task MapToAgentContext_NoFrameworkTypeInOptions_UsesConfiguredClientType()
    {
        // Arrange — config says AzureAIInference, options has no override
        var factory = CreateFactory(AIAgentFrameworkClientType.AzureAIInference);
        var options = new SkillAgentOptions(); // FrameworkType is null

        // Act
        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        // Assert — must read from config, not hardcode AzureOpenAI
        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.AzureAIInference);
    }

    [Fact]
    public async Task MapToAgentContext_FrameworkTypeInOptions_OverridesConfig()
    {
        // Arrange — config says AzureAIInference, options explicitly overrides to OpenAI
        var factory = CreateFactory(AIAgentFrameworkClientType.AzureAIInference);
        var options = new SkillAgentOptions
        {
            FrameworkType = AIAgentFrameworkClientType.OpenAI
        };

        // Act
        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        // Assert — options override wins
        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.OpenAI);
    }

    [Fact]
    public async Task MapToAgentContext_NoFrameworkTypeAnywhere_DefaultsToAzureOpenAI()
    {
        // Arrange — config has no ClientType set (null/default)
        var appConfig = new AppConfig { AI = new AIConfig { AgentFramework = new AgentFrameworkConfig() } };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance);

        var options = new SkillAgentOptions();

        // Act
        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        // Assert — falls back to AzureOpenAI as last resort
        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
    }
}
