using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.AI.Tools;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolChainBuilder"/> covering all three resolution modes
/// (Injected, Declarations, AllowedTools), MCP-first with keyed DI fallback,
/// plugin governance boundary filtering, and cross-skill deduplication.
/// </summary>
public class ToolChainBuilderTests
{
    private static ToolChainBuilder CreateBuilder(
        IMcpToolProvider? mcpToolProvider = null,
        IToolConverter? toolConverter = null,
        IServiceProvider? serviceProvider = null)
    {
        return new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance,
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            toolConverter,
            mcpToolProvider);
    }

    // --- Injected mode ---

    [Fact]
    public async Task BuildToolsAsync_InjectedMode_GetsAllMcpTools()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["server-a"] = [AIFunctionFactory.Create(() => "r", "tool_a")],
                ["server-b"] = [AIFunctionFactory.Create(() => "r", "tool_b")]
            });

        var builder = CreateBuilder(mcpToolProvider: mcpProvider.Object);
        var skill = new SkillDefinition
        {
            Id = "plugin-skill", Name = "plugin-skill",
            Instructions = "Test", PluginSource = "plugin"
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().HaveCount(2);
        tools.Select(t => t.Name).Should().BeEquivalentTo(["tool_a", "tool_b"]);
    }

    [Fact]
    public async Task BuildToolsAsync_InjectedMode_DeduplicatesByName()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["server-a"] = [AIFunctionFactory.Create(() => "a", "dup_tool")],
                ["server-b"] = [AIFunctionFactory.Create(() => "b", "dup_tool")]
            });

        var builder = CreateBuilder(mcpToolProvider: mcpProvider.Object);
        var skill = new SkillDefinition
        {
            Id = "dedup", Name = "dedup", Instructions = "Test", PluginSource = "p"
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().ContainSingle(t => t.Name == "dup_tool");
    }

    // --- Plugin governance ---

    [Fact]
    public async Task BuildToolsAsync_InjectedMode_AllowedToolsFiltersToWhitelist()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["s"] = [
                    AIFunctionFactory.Create(() => "r", "az_cli"),
                    AIFunctionFactory.Create(() => "r", "bash"),
                    AIFunctionFactory.Create(() => "r", "deploy")
                ]
            });

        var pluginRegistry = new Mock<IPluginRegistry>();
        pluginRegistry.Setup(r => r.GetPlugin("azure")).Returns(
            new LoadedPlugin("azure", "1.0", "/plugins/azure", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["azure:server"],
                new PluginDeclaration { Name = "azure", AllowedTools = ["az_cli"] }));

        var services = new ServiceCollection();
        services.AddSingleton(pluginRegistry.Object);

        var builder = CreateBuilder(
            mcpToolProvider: mcpProvider.Object,
            serviceProvider: services.BuildServiceProvider());

        var skill = new SkillDefinition
        {
            Id = "azure-skill", Name = "azure-skill",
            Instructions = "Deploy", PluginSource = "azure"
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().ContainSingle(t => t.Name == "az_cli");
    }

    [Fact]
    public async Task BuildToolsAsync_InjectedMode_DeniedToolsRemovesBlacklisted()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["s"] = [
                    AIFunctionFactory.Create(() => "r", "safe"),
                    AIFunctionFactory.Create(() => "r", "dangerous")
                ]
            });

        var pluginRegistry = new Mock<IPluginRegistry>();
        pluginRegistry.Setup(r => r.GetPlugin("p")).Returns(
            new LoadedPlugin("p", "1.0", "/plugins/p", new PluginManifest(),
                PluginLoadStatus.Loaded, [], ["p:server"],
                new PluginDeclaration { Name = "p", DeniedTools = ["dangerous"] }));

        var services = new ServiceCollection();
        services.AddSingleton(pluginRegistry.Object);

        var builder = CreateBuilder(
            mcpToolProvider: mcpProvider.Object,
            serviceProvider: services.BuildServiceProvider());

        var skill = new SkillDefinition
        {
            Id = "p-skill", Name = "p-skill",
            Instructions = "Test", PluginSource = "p"
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().ContainSingle(t => t.Name == "safe");
    }

    // --- Managed mode: pre-created tools ---

    [Fact]
    public async Task BuildToolsAsync_PreCreatedTools_IncludesDirectly()
    {
        var builder = CreateBuilder();
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            Tools = [AIFunctionFactory.Create(() => "ok", "my_tool")]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().ContainSingle(t => t.Name == "my_tool");
    }

    // --- Managed mode: tool declarations ---

    [Fact]
    public async Task BuildToolsAsync_ToolDeclaration_TriesMcpFirst()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetToolsAsync("search", It.IsAny<CancellationToken>()))
            .ReturnsAsync([AIFunctionFactory.Create(() => "mcp", "search")]);

        var builder = CreateBuilder(mcpToolProvider: mcpProvider.Object);
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            ToolDeclarations = [new ToolDeclaration { Name = "search" }]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().Contain(t => t.Name == "search");
    }

    [Fact]
    public async Task BuildToolsAsync_ToolDeclaration_FallsBackToKeyedDI()
    {
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetToolsAsync("calc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AITool>());

        var toolMock = new Mock<ITool>();
        toolMock.Setup(t => t.Name).Returns("calc");
        toolMock.Setup(t => t.Description).Returns("Calculator");
        toolMock.Setup(t => t.SupportedOperations).Returns(["add"]);

        var convertedTool = AIFunctionFactory.Create(() => "converted", "calc");
        var converter = new Mock<IToolConverter>();
        converter.Setup(c => c.Convert(toolMock.Object, null)).Returns(convertedTool);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("calc", toolMock.Object);

        var builder = CreateBuilder(
            mcpToolProvider: mcpProvider.Object,
            toolConverter: converter.Object,
            serviceProvider: services.BuildServiceProvider());

        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            ToolDeclarations = [new ToolDeclaration { Name = "calc" }]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().Contain(t => t.Name == "calc");
    }

    [Fact]
    public async Task BuildToolsAsync_RequiredToolUnresolvable_Throws()
    {
        var builder = CreateBuilder();
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            ToolDeclarations = [new ToolDeclaration { Name = "missing", Optional = false }]
        };

        var act = () => builder.BuildToolsAsync(skill, new SkillAgentOptions());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*could not be resolved*");
    }

    [Fact]
    public async Task BuildToolsAsync_OptionalToolUnresolvable_Succeeds()
    {
        var builder = CreateBuilder();
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            ToolDeclarations = [new ToolDeclaration { Name = "optional", Optional = true }]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
    }

    // --- Merged tools ---

    [Fact]
    public async Task BuildMergedToolsAsync_MultipleSkills_DeduplicatesAcrossSkills()
    {
        var builder = CreateBuilder();
        var skills = new List<SkillDefinition>
        {
            new() { Id = "s1", Name = "S1", Tools = [AIFunctionFactory.Create(() => "a", "shared")] },
            new() { Id = "s2", Name = "S2", Tools = [AIFunctionFactory.Create(() => "b", "shared")] }
        };

        var tools = await builder.BuildMergedToolsAsync(skills, new SkillAgentOptions());

        tools.Should().ContainSingle(t => t.Name == "shared");
    }

    [Fact]
    public async Task BuildMergedToolsAsync_WithAllowedToolsWhitelist_FiltersResults()
    {
        var builder = CreateBuilder();
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Id = "s1", Name = "S1",
                Tools = [
                    AIFunctionFactory.Create(() => "a", "tool_a"),
                    AIFunctionFactory.Create(() => "b", "tool_b")
                ]
            }
        };

        var tools = await builder.BuildMergedToolsAsync(skills, new SkillAgentOptions(), ["tool_a"]);

        tools.Should().ContainSingle(t => t.Name == "tool_a");
    }

    // --- Additional tools from options ---

    [Fact]
    public async Task BuildToolsAsync_AdditionalToolsFromOptions_Included()
    {
        var builder = CreateBuilder();
        var skill = new SkillDefinition { Id = "s", Name = "s", Instructions = "Test" };
        var options = new SkillAgentOptions
        {
            AdditionalTools = [AIFunctionFactory.Create(() => "extra", "extra_tool")]
        };

        var tools = await builder.BuildToolsAsync(skill, options);

        tools.Should().ContainSingle(t => t.Name == "extra_tool");
    }
}
