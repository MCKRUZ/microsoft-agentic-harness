using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// End-to-end tests proving that plugin boundary governance is actually wired through the
/// real skill-discovery path — not just the unit tests that pass <c>pluginSource</c> explicitly.
/// </summary>
/// <remarks>
/// These tests exercise the production wiring: a plugin is registered in <see cref="PluginRegistry"/>
/// with its skills directory recorded in <see cref="LoadedPlugin.SkillPaths"/>, and
/// <see cref="SkillMetadataRegistry"/> discovers a SKILL.md under that directory. The regression they
/// guard against is that <c>SkillMetadataRegistry.Discover</c> never told the parser which plugin owned
/// a skill, so <see cref="SkillDefinition.PluginSource"/> stayed null, every skill collapsed to
/// <see cref="SkillMode.Managed"/>, and the plugin's <c>DeniedTools</c> boundary never fired.
/// </remarks>
public sealed class PluginGovernanceWiringTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _pluginSkillsDir;

    public PluginGovernanceWiringTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "plugin-gov-" + Guid.NewGuid().ToString("N"));
        _pluginSkillsDir = Path.Combine(_tempRoot, "myplugin", "skills");
        Directory.CreateDirectory(_pluginSkillsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private void WriteSkill(string skillFolder, string skillMarkdown)
    {
        var dir = Path.Combine(_pluginSkillsDir, skillFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMarkdown);
    }

    private static PluginRegistry RegistryWithPlugin(string skillsDir, PluginDeclaration declaration)
    {
        var registry = new PluginRegistry();
        registry.Register(new LoadedPlugin(
            declaration.Name,
            "1.0",
            Path.GetDirectoryName(skillsDir)!,
            new PluginManifest { Name = declaration.Name, Skills = "./skills/" },
            PluginLoadStatus.Loaded,
            SkillPaths: [skillsDir],
            McpServerNames: [],
            declaration));
        return registry;
    }

    private SkillMetadataRegistry CreateSkillRegistry(IPluginRegistry pluginRegistry)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig { Skills = new SkillsConfig { BasePath = _pluginSkillsDir } }
        };
        return new SkillMetadataRegistry(
            NullLogger<SkillMetadataRegistry>.Instance,
            new OptionsMonitorStub(appConfig),
            new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance),
            pluginRegistry);
    }

    [Fact]
    public void Discover_SkillUnderPluginSkillPath_PopulatesPluginSourceAndInjectedMode()
    {
        WriteSkill("do-thing", """
            ---
            name: injected-plugin-skill
            description: A plugin skill with no tool declarations.
            ---
            Do the thing.
            """);

        var pluginRegistry = RegistryWithPlugin(
            _pluginSkillsDir,
            new PluginDeclaration { Name = "myplugin", DeniedTools = ["dangerous"] });
        var skillRegistry = CreateSkillRegistry(pluginRegistry);

        var skill = skillRegistry.TryGet("injected-plugin-skill");

        skill.Should().NotBeNull();
        skill!.PluginSource.Should().Be("myplugin",
            "the discovered skill lives under the plugin's SkillPaths and must be attributed to it");
        skill.Mode.Should().Be(SkillMode.Injected,
            "a plugin skill with no tool declarations resolves in Injected mode");
    }

    [Fact]
    public async Task DeniedTool_IsFilteredOut_ForDiscoveredPluginSkill()
    {
        WriteSkill("do-thing", """
            ---
            name: injected-plugin-skill
            description: A plugin skill with no tool declarations.
            ---
            Do the thing.
            """);

        var declaration = new PluginDeclaration { Name = "myplugin", DeniedTools = ["dangerous"] };
        var pluginRegistry = RegistryWithPlugin(_pluginSkillsDir, declaration);
        var skillRegistry = CreateSkillRegistry(pluginRegistry);
        var skill = skillRegistry.TryGet("injected-plugin-skill");
        skill.Should().NotBeNull();

        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IList<AITool>>
            {
                ["server"] =
                [
                    AIFunctionFactory.Create(() => "r", "safe"),
                    AIFunctionFactory.Create(() => "r", "dangerous")
                ]
            });

        var services = new ServiceCollection();
        services.AddSingleton<IPluginRegistry>(pluginRegistry);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance,
            services.BuildServiceProvider(),
            toolConverter: null,
            mcpToolProvider: mcpProvider.Object);

        var tools = await builder.BuildToolsAsync(skill!, new SkillAgentOptions());

        tools.Select(t => t.Name).Should().NotContain("dangerous",
            "the plugin's DeniedTools boundary must filter the tool out of the resolved set");
        tools.Select(t => t.Name).Should().Contain("safe");
    }

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
