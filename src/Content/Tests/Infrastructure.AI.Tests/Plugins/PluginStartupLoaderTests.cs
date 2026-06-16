using System.Text.Json;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// End-to-end regression tests for <see cref="PluginStartupLoader"/>. These exercise the full
/// real pipeline — <see cref="PluginManifestReader"/>, <see cref="PluginLoader"/> wired to the
/// live <c>AppConfig</c> instances, and <see cref="PluginRegistry"/> — so a configured plugin
/// package actually lands in the registry and the live config after the hosted service runs.
/// </summary>
public sealed class PluginStartupLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public PluginStartupLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"plugin-startup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task StartAsync_ConfiguredPluginPackage_RegistersPluginAndMergesConfig()
    {
        // Arrange: a plugin directory with a manifest declaring skills and MCP servers.
        var pluginDir = Path.Combine(_tempDir, "azure");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(Path.Combine(pluginDir, "skills"));

        WriteManifest(pluginDir, new PluginManifest
        {
            Name = "azure",
            Version = "2.1.0",
            Skills = "./skills/",
            McpServers = "./.mcp.json"
        });
        WriteMcpConfig(pluginDir, serverName: "ai", command: "npx");

        var config = MakeConfig(new PluginDeclaration
        {
            Name = "azure",
            Path = pluginDir,
            Enabled = true
        });
        var registry = new PluginRegistry();
        var sut = BuildSut(config, registry);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert: the plugin is in the registry as Loaded, and its skill path + namespaced MCP
        // server were merged into the SAME config instances downstream consumers read.
        registry.IsLoaded("azure").Should().BeTrue();
        var loaded = registry.GetPlugin("azure");
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be("2.1.0");

        config.CurrentValue.AI.Skills.AdditionalPaths
            .Should().ContainSingle()
            .Which.Should().Be(Path.Combine(pluginDir, "skills"));
        config.CurrentValue.AI.McpServers.Servers.Should().ContainKey("azure:ai");
        config.CurrentValue.AI.McpServers.Servers["azure:ai"].Command.Should().Be("npx");
    }

    [Fact]
    public async Task StartAsync_EmptyPackages_IsCleanNoOp()
    {
        var config = MakeConfig();
        var registry = new PluginRegistry();
        var sut = BuildSut(config, registry);

        await sut.StartAsync(CancellationToken.None);

        registry.GetLoadedPlugins().Should().BeEmpty();
        config.CurrentValue.AI.Skills.AdditionalPaths.Should().BeEmpty();
        config.CurrentValue.AI.McpServers.Servers.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_DisabledPackage_RecordedAsDisabledAndNotMerged()
    {
        var pluginDir = Path.Combine(_tempDir, "disabled");
        Directory.CreateDirectory(pluginDir);
        WriteManifest(pluginDir, new PluginManifest { Name = "disabled", Version = "1.0.0" });

        var config = MakeConfig(new PluginDeclaration
        {
            Name = "disabled",
            Path = pluginDir,
            Enabled = false
        });
        var registry = new PluginRegistry();
        var sut = BuildSut(config, registry);

        await sut.StartAsync(CancellationToken.None);

        registry.IsLoaded("disabled").Should().BeFalse();
        registry.GetPlugin("disabled")!.Status.Should().Be(PluginLoadStatus.Disabled);
        config.CurrentValue.AI.Skills.AdditionalPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_MissingManifest_SkipsWithoutRegistering()
    {
        var pluginDir = Path.Combine(_tempDir, "no-manifest");
        Directory.CreateDirectory(pluginDir);

        var config = MakeConfig(new PluginDeclaration
        {
            Name = "no-manifest",
            Path = pluginDir,
            Enabled = true
        });
        var registry = new PluginRegistry();
        var sut = BuildSut(config, registry);

        await sut.StartAsync(CancellationToken.None);

        registry.GetPlugin("no-manifest").Should().BeNull();
    }

    private static PluginStartupLoader BuildSut(IOptionsMonitor<AppConfig> config, PluginRegistry registry)
    {
        // PluginLoader is wired to the live config instances, mirroring the DI registration:
        // it mutates AI.Skills / AI.McpServers in place so consumers observe the merge.
        var loader = new PluginLoader(
            config.CurrentValue.AI.Skills,
            config.CurrentValue.AI.McpServers,
            NullLogger<PluginLoader>.Instance);

        return new PluginStartupLoader(
            config,
            new PluginManifestReader(NullLogger<PluginManifestReader>.Instance),
            loader,
            registry,
            NullLogger<PluginStartupLoader>.Instance);
    }

    private static IOptionsMonitor<AppConfig> MakeConfig(params PluginDeclaration[] packages)
    {
        var cfg = new AppConfig();
        cfg.AI.Plugins.Packages = packages;
        return new StaticOptionsMonitor(cfg);
    }

    private static void WriteManifest(string pluginDir, PluginManifest manifest) =>
        File.WriteAllText(
            Path.Combine(pluginDir, "plugin.json"),
            JsonSerializer.Serialize(manifest));

    private static void WriteMcpConfig(string pluginDir, string serverName, string command)
    {
        var mcp = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [serverName] = new { command, args = new[] { "run" } }
            }
        };
        File.WriteAllText(Path.Combine(pluginDir, ".mcp.json"), JsonSerializer.Serialize(mcp));
    }

    private sealed class StaticOptionsMonitor(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
