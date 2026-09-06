using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

public class PluginRegistryTests
{
    private readonly PluginRegistry _sut = new();

    private static LoadedPlugin MakePlugin(string name, PluginLoadStatus status = PluginLoadStatus.Loaded) =>
        new(name, "1.0.0", $"/plugins/{name}", new PluginManifest { Name = name, Version = "1.0.0" },
            status, [], [], new PluginDeclaration { Name = name });

    [Fact]
    public void GetLoadedPlugins_Initially_ReturnsEmpty()
    {
        _sut.GetLoadedPlugins().Should().BeEmpty();
    }

    [Fact]
    public void Register_ThenGetPlugin_ReturnsPlugin()
    {
        var plugin = MakePlugin("azure");
        _sut.Register(plugin);

        _sut.GetPlugin("azure").Should().Be(plugin);
    }

    [Fact]
    public void IsLoaded_RegisteredPlugin_ReturnsTrue()
    {
        _sut.Register(MakePlugin("azure"));

        _sut.IsLoaded("azure").Should().BeTrue();
    }

    [Fact]
    public void IsLoaded_UnregisteredPlugin_ReturnsFalse()
    {
        _sut.IsLoaded("missing").Should().BeFalse();
    }

    [Fact]
    public void GetPlugin_CaseInsensitive_ReturnsPlugin()
    {
        _sut.Register(MakePlugin("Azure"));

        _sut.GetPlugin("azure").Should().NotBeNull();
        _sut.GetPlugin("AZURE").Should().NotBeNull();
    }

    [Fact]
    public void GetLoadedPlugins_MultiplePlugins_ReturnsAll()
    {
        _sut.Register(MakePlugin("a"));
        _sut.Register(MakePlugin("b"));
        _sut.Register(MakePlugin("c"));

        _sut.GetLoadedPlugins().Should().HaveCount(3);
    }

    [Fact]
    public void IsLoaded_FailedPlugin_ReturnsFalse()
    {
        _sut.Register(MakePlugin("broken", PluginLoadStatus.Failed));

        _sut.IsLoaded("broken").Should().BeFalse();
    }

    [Fact]
    public void IsBoundaryFaulted_UnmarkedPlugin_ReturnsFalse()
    {
        _sut.IsBoundaryFaulted("azure").Should().BeFalse();
    }

    [Fact]
    public void MarkBoundaryFaulted_ThenIsBoundaryFaulted_ReturnsTrue()
    {
        _sut.MarkBoundaryFaulted("azure", "DeniedTools entry 'file_wrte' matches no known tool");

        _sut.IsBoundaryFaulted("azure").Should().BeTrue();
    }

    [Fact]
    public void IsBoundaryFaulted_CaseInsensitive_ReturnsTrue()
    {
        _sut.MarkBoundaryFaulted("Azure", "reason");

        _sut.IsBoundaryFaulted("azure").Should().BeTrue();
        _sut.IsBoundaryFaulted("AZURE").Should().BeTrue();
    }
}
