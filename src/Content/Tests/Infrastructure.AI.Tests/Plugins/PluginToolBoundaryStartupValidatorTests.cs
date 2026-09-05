using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// #524: <see cref="PluginToolBoundaryStartupValidator"/> refuses to boot when
/// <see cref="IPluginToolBoundaryTracker.Seed"/> reports an immediately-provable violation (no MCP
/// server configured anywhere on the host, and an AllowedTools/DeniedTools entry matches no
/// first-party tool).
/// </summary>
public sealed class PluginToolBoundaryStartupValidatorTests
{
    private readonly Mock<IPluginRegistry> _registry = new();
    private readonly Mock<IPluginToolBoundaryTracker> _tracker = new();

    private static LoadedPlugin MakePlugin(string name) =>
        new(name, "1.0.0", $"/plugins/{name}", new PluginManifest { Name = name, Version = "1.0.0" },
            PluginLoadStatus.Loaded, [], [], new PluginDeclaration { Name = name, DeniedTools = ["file_wrte"] });

    private static IOptionsMonitor<AIConfig> MakeAiConfig(params string[] enabledServerNames)
    {
        var servers = new ConcurrentDictionary<string, McpServerDefinition>(
            enabledServerNames.ToDictionary(n => n, _ => new McpServerDefinition { Enabled = true }));
        var config = new AIConfig { McpServers = new McpServersConfig { Servers = servers } };
        var monitor = new Mock<IOptionsMonitor<AIConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }

    private PluginToolBoundaryStartupValidator MakeSut(
        Func<string, bool> isKnownFirstPartyToolName, IOptionsMonitor<AIConfig>? aiConfig = null) =>
        new(_registry.Object, _tracker.Object, isKnownFirstPartyToolName,
            aiConfig ?? MakeAiConfig(), NullLogger<PluginToolBoundaryStartupValidator>.Instance);

    [Fact]
    public async Task StartAsync_SeedReturnsNoViolations_DoesNotThrow()
    {
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        var sut = MakeSut(_ => true);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SeedReturnsAnImmediateViolation_ThrowsNamingPluginAndTool()
    {
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([new PluginToolBoundaryViolation("azure", "DeniedTools", "file_wrte")]);
        var sut = MakeSut(_ => false);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*azure*").WithMessage("*DeniedTools*").WithMessage("*file_wrte*");
    }

    [Fact]
    public async Task StartAsync_OnlyPassesLoadedPluginsToSeed_ExcludingDisabledAndFailed()
    {
        var loaded = MakePlugin("azure");
        var disabled = new LoadedPlugin("disabled-plugin", "", "/plugins/disabled",
            new PluginManifest { Name = "disabled-plugin" }, PluginLoadStatus.Disabled, [], [],
            new PluginDeclaration { Name = "disabled-plugin" });
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([loaded, disabled]);
        IReadOnlyList<LoadedPlugin>? captured = null;
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Callback<IReadOnlyList<LoadedPlugin>, Func<string, bool>, IReadOnlyCollection<string>>(
                (plugins, _, _) => captured = plugins)
            .Returns([]);
        var sut = MakeSut(_ => true);

        await sut.StartAsync(CancellationToken.None);

        captured.Should().ContainSingle().Which.Name.Should().Be("azure");
    }

    [Fact]
    public async Task StartAsync_PassesOnlyEnabledConfiguredServerNamesToSeed()
    {
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        IReadOnlyCollection<string>? captured = null;
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Callback<IReadOnlyList<LoadedPlugin>, Func<string, bool>, IReadOnlyCollection<string>>(
                (_, _, servers) => captured = servers)
            .Returns([]);
        var servers = new ConcurrentDictionary<string, McpServerDefinition>
        {
            ["enabled-server"] = new() { Enabled = true },
            ["disabled-server"] = new() { Enabled = false },
        };
        var aiConfig = new Mock<IOptionsMonitor<AIConfig>>();
        aiConfig.Setup(m => m.CurrentValue).Returns(new AIConfig { McpServers = new McpServersConfig { Servers = servers } });
        var sut = MakeSut(_ => true, aiConfig.Object);

        await sut.StartAsync(CancellationToken.None);

        captured.Should().ContainSingle().Which.Should().Be("enabled-server");
    }
}
