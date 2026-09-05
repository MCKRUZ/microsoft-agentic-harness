using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// #524: <see cref="PluginToolBoundaryStartupValidator"/> refuses to boot when
/// <see cref="IPluginToolBoundaryTracker.Seed"/> reports an immediately-provable violation (a
/// plugin with no MCP servers whose AllowedTools/DeniedTools entry matches no first-party tool).
/// </summary>
public sealed class PluginToolBoundaryStartupValidatorTests
{
    private readonly Mock<IPluginRegistry> _registry = new();
    private readonly Mock<IPluginToolBoundaryTracker> _tracker = new();

    private static LoadedPlugin MakePlugin(string name) =>
        new(name, "1.0.0", $"/plugins/{name}", new PluginManifest { Name = name, Version = "1.0.0" },
            PluginLoadStatus.Loaded, [], [], new PluginDeclaration { Name = name, DeniedTools = ["file_wrte"] });

    private PluginToolBoundaryStartupValidator MakeSut(Func<string, bool> isKnownFirstPartyToolName) =>
        new(_registry.Object, _tracker.Object, isKnownFirstPartyToolName,
            NullLogger<PluginToolBoundaryStartupValidator>.Instance);

    [Fact]
    public async Task StartAsync_SeedReturnsNoViolations_DoesNotThrow()
    {
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>()))
            .Returns([]);
        var sut = MakeSut(_ => true);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SeedReturnsAnImmediateViolation_ThrowsNamingPluginAndTool()
    {
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>()))
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
        _tracker.Setup(t => t.Seed(It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>()))
            .Callback<IReadOnlyList<LoadedPlugin>, Func<string, bool>>((plugins, _) => captured = plugins)
            .Returns([]);
        var sut = MakeSut(_ => true);

        await sut.StartAsync(CancellationToken.None);

        captured.Should().ContainSingle().Which.Name.Should().Be("azure");
    }
}
