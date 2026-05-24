using Application.AI.Common.Factories;
using Domain.AI.Skills;
using Domain.AI.Tools;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for <see cref="AgentExecutionContextFactory"/> required-tool enforcement.
/// Verifies that unresolvable required tools throw, while optional tools and manual-fallback
/// tools silently continue.
/// </summary>
public class AgentExecutionContextFactoryToolEnforcementTests
{
    private readonly AgentExecutionContextFactory _factory;

    public AgentExecutionContextFactoryToolEnforcementTests()
    {
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

        _factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task MapToAgentContextAsync_RequiredToolUnresolvable_ThrowsInvalidOperation()
    {
        var skill = new SkillDefinition
        {
            Id = "deploy",
            Name = "deploy",
            Instructions = "Deploy things",
            ToolDeclarations = [new ToolDeclaration { Name = "deploy_execute", Optional = false }]
        };

        var act = () => _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deploy_execute*could not be resolved*");
    }

    [Fact]
    public async Task MapToAgentContextAsync_OptionalToolUnresolvable_Succeeds()
    {
        var skill = new SkillDefinition
        {
            Id = "research",
            Name = "research",
            Instructions = "Research things",
            ToolDeclarations = [new ToolDeclaration { Name = "optional_helper", Optional = true }]
        };

        var context = await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());
        context.Should().NotBeNull();
    }

    [Fact]
    public async Task MapToAgentContextAsync_RequiredToolWithManualFallback_Succeeds()
    {
        var skill = new SkillDefinition
        {
            Id = "review",
            Name = "review",
            Instructions = "Review code",
            ToolDeclarations = [new ToolDeclaration { Name = "missing_tool", Optional = false, Fallback = "manual" }]
        };

        var context = await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());
        context.Should().NotBeNull();
    }

    [Fact]
    public async Task MapToAgentContextAsync_RequiredToolWithNamedFallbackBothMissing_Throws()
    {
        var skill = new SkillDefinition
        {
            Id = "deploy",
            Name = "deploy",
            Instructions = "Deploy things",
            ToolDeclarations = [new ToolDeclaration { Name = "deploy_execute", Optional = false, Fallback = "deploy_fallback" }]
        };

        var act = () => _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deploy_execute*could not be resolved*");
    }

    [Fact]
    public async Task MapToAgentContextAsync_NoToolDeclarations_Succeeds()
    {
        var skill = new SkillDefinition
        {
            Id = "simple",
            Name = "simple",
            Instructions = "Do simple things"
        };

        var context = await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());
        context.Should().NotBeNull();
    }
}
