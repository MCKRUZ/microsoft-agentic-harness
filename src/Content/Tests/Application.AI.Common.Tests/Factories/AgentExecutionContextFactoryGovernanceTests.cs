using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for provisioning-time plugin boundary filtering in <see cref="AgentExecutionContextFactory"/>.
/// Injected-mode skills resolve all MCP tools then apply AllowedTools/DeniedTools from the plugin declaration.
/// </summary>
public sealed class AgentExecutionContextFactoryGovernanceTests
{
    private readonly Mock<IMcpToolProvider> _mcpToolProvider = new();
    private readonly Mock<IPluginRegistry> _pluginRegistry = new();

    private AgentExecutionContextFactory CreateFactory()
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

        var services = new ServiceCollection();
        services.AddSingleton(_pluginRegistry.Object);
        var sp = services.BuildServiceProvider();

        var toolChainBuilder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance,
            sp,
            mcpToolProvider: _mcpToolProvider.Object);

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            sp,
            NullLoggerFactory.Instance,
            toolChainBuilder,
            new SkillPrerequisiteResolver());
    }

    private void SetupMcpTools(params (string server, string[] tools)[] servers)
    {
        var dict = new Dictionary<string, IList<AITool>>();
        foreach (var (server, tools) in servers)
        {
            dict[server] = tools
                .Select(t => (AITool)AIFunctionFactory.Create(() => "r", t))
                .ToList();
        }
        _mcpToolProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);
    }

    private void SetupPlugin(string name, PluginDeclaration declaration)
    {
        var plugin = new LoadedPlugin(
            name, "1.0", $"/plugins/{name}", new PluginManifest(),
            PluginLoadStatus.Loaded, [], [$"{name}:server"], declaration);
        _pluginRegistry
            .Setup(r => r.GetPlugin(name))
            .Returns(plugin);
    }

    [Fact]
    public async Task Injected_AllowedTools_FiltersToWhitelist()
    {
        SetupMcpTools(("azure:server", ["az_cli", "bash", "deploy", "read_file"]));
        SetupPlugin("azure", new PluginDeclaration
        {
            Name = "azure",
            AllowedTools = ["az_cli", "read_file"]
        });

        var skill = new SkillDefinition
        {
            Id = "azure-skill", Name = "azure-skill",
            Instructions = "Deploy", PluginSource = "azure"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["az_cli", "read_file"]);
    }

    [Fact]
    public async Task Injected_DeniedTools_RemovesBlacklisted()
    {
        SetupMcpTools(("plugin:server", ["tool_a", "tool_b", "dangerous"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            DeniedTools = ["dangerous"]
        });

        var skill = new SkillDefinition
        {
            Id = "plugin-skill", Name = "plugin-skill",
            Instructions = "Do stuff", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["tool_a", "tool_b"]);
    }

    [Fact]
    public async Task Injected_DeniedWinsOverAllowed()
    {
        SetupMcpTools(("plugin:server", ["a", "b", "c"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            AllowedTools = ["a", "b"],
            DeniedTools = ["b"]
        });

        var skill = new SkillDefinition
        {
            Id = "conflict-skill", Name = "conflict-skill",
            Instructions = "Test", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task Injected_NoGovernanceConfig_AllToolsPassThrough()
    {
        SetupMcpTools(("azure:server", ["tool_1", "tool_2"]));
        _pluginRegistry
            .Setup(r => r.GetPlugin("azure"))
            .Returns((LoadedPlugin?)null);

        var skill = new SkillDefinition
        {
            Id = "ungoverned", Name = "ungoverned",
            Instructions = "No governance", PluginSource = "azure"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().HaveCount(2);
    }

    [Fact]
    public async Task Injected_AllowedToolsOnly_NoMatchingTools_ReturnsEmpty()
    {
        SetupMcpTools(("plugin:server", ["tool_a", "tool_b"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            AllowedTools = ["nonexistent"]
        });

        var skill = new SkillDefinition
        {
            Id = "strict", Name = "strict",
            Instructions = "Strict", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Injected_AdditionalTools_SubjectToGovernanceFiltering()
    {
        SetupMcpTools(("plugin:server", ["safe_tool"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            DeniedTools = ["dangerous"]
        });

        var skill = new SkillDefinition
        {
            Id = "bypass-test", Name = "bypass-test",
            Instructions = "Test", PluginSource = "plugin"
        };

        var options = new SkillAgentOptions
        {
            AdditionalTools = [AIFunctionFactory.Create(() => "r", "dangerous")]
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, options);

        context.Tools.Select(t => t.Name).Should().BeEquivalentTo(["safe_tool"]);
        context.Tools.Should().NotContain(t => t.Name == "dangerous");
    }

    [Fact]
    public async Task Injected_AllowedTools_CaseInsensitiveMatching()
    {
        SetupMcpTools(("plugin:server", ["az_cli", "Read_File", "SEARCH"]));
        SetupPlugin("plugin", new PluginDeclaration
        {
            Name = "plugin",
            AllowedTools = ["AZ_CLI", "read_file"]
        });

        var skill = new SkillDefinition
        {
            Id = "case-test", Name = "case-test",
            Instructions = "Test", PluginSource = "plugin"
        };

        var context = await CreateFactory().MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().HaveCount(2);
        context.Tools.Select(t => t.Name).Should().Contain("az_cli");
        context.Tools.Select(t => t.Name).Should().Contain("Read_File");
    }
}
