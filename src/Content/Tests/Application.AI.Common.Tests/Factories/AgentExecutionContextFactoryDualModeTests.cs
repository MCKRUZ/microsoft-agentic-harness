using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for dual skill mode in <see cref="AgentExecutionContextFactory"/>.
/// Injected mode: plugin skills with no tool declarations receive all MCP tools.
/// Managed mode: skills with AllowedTools or ToolDeclarations use standard resolution.
/// </summary>
public class AgentExecutionContextFactoryDualModeTests
{
    private readonly Mock<IMcpToolProvider> _mcpToolProvider;
    private readonly AgentExecutionContextFactory _factory;

    public AgentExecutionContextFactoryDualModeTests()
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

        _mcpToolProvider = new Mock<IMcpToolProvider>();

        _factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            mcpToolProvider: _mcpToolProvider.Object);
    }

    [Fact]
    public async Task MapToAgentContextAsync_InjectedPluginSkill_GetsAllMcpTools()
    {
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["azure:azure"] = new List<AITool>
                {
                    AIFunctionFactory.Create(() => "result", "azure_deploy"),
                    AIFunctionFactory.Create(() => "result", "azure_validate")
                }
            });

        var skill = new SkillDefinition
        {
            Id = "azure-deploy",
            Name = "azure-deploy",
            Instructions = "Deploy Azure resources",
            PluginSource = "azure"
            // No AllowedTools, no ToolDeclarations — injected mode
        };

        var context = await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().HaveCount(2);
        context.Tools.Should().Contain(t => t.Name == "azure_deploy");
        context.Tools.Should().Contain(t => t.Name == "azure_validate");
    }

    [Fact]
    public async Task MapToAgentContextAsync_ManagedSkillWithAllowedTools_DoesNotGetAllMcpTools()
    {
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>());

        var skill = new SkillDefinition
        {
            Id = "managed-skill",
            Name = "managed-skill",
            Instructions = "Do managed things",
            PluginSource = "some-plugin",
            AllowedTools = ["tool_a"]
        };

        await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        // Has AllowedTools — managed mode, must not call GetAllToolsAsync
        _mcpToolProvider.Verify(
            p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MapToAgentContextAsync_NonPluginSkillNoTools_DoesNotGetAllMcpTools()
    {
        var skill = new SkillDefinition
        {
            Id = "native-skill",
            Name = "native-skill",
            Instructions = "Native skill"
            // No PluginSource — harness-native skill, not plugin
        };

        await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        _mcpToolProvider.Verify(
            p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MapToAgentContextAsync_InjectedMode_DeduplicatesToolsByName()
    {
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["server-a"] = new List<AITool>
                {
                    AIFunctionFactory.Create(() => "a", "read_file"),
                    AIFunctionFactory.Create(() => "a", "write_file")
                },
                ["server-b"] = new List<AITool>
                {
                    AIFunctionFactory.Create(() => "b", "read_file"),
                    AIFunctionFactory.Create(() => "b", "search")
                }
            });

        var skill = new SkillDefinition
        {
            Id = "dup-test",
            Name = "dup-test",
            Instructions = "Test dedup",
            PluginSource = "test-plugin"
        };

        var context = await _factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().HaveCount(3);
        context.Tools.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task MapToAgentContextAsync_MixedManagedAndInjected_MergesTools()
    {
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["azure:azure"] = new List<AITool>
                {
                    AIFunctionFactory.Create(() => "r", "plugin_tool_1"),
                    AIFunctionFactory.Create(() => "r", "plugin_tool_2")
                }
            });

        var managedSkill = new SkillDefinition
        {
            Id = "research",
            Name = "research",
            Instructions = "Research things",
            Tools = [AIFunctionFactory.Create(() => "r", "search_tool")]
        };
        var injectedSkill = new SkillDefinition
        {
            Id = "azure-deploy",
            Name = "azure-deploy",
            Instructions = "Deploy",
            PluginSource = "azure"
        };

        var context = await _factory.MapToAgentContextAsync(
            [managedSkill, injectedSkill], new SkillAgentOptions());

        context.Tools.Should().HaveCount(3);
        context.Tools.Should().Contain(t => t.Name == "search_tool");
        context.Tools.Should().Contain(t => t.Name == "plugin_tool_1");
        context.Tools.Should().Contain(t => t.Name == "plugin_tool_2");
    }
}
