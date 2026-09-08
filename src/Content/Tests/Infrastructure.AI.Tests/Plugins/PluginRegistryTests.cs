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
    public void GetBoundaryStatus_NeverMarked_ReturnsPending()
    {
        // #613: a plugin PluginToolBoundaryTracker.Seed hasn't processed yet (startup race, or any
        // other bug that leaves a loaded plugin unseeded) must default to the fail-closed state, not
        // to Verified — Verified means "proven safe," and nothing has proven anything about a plugin
        // never marked. Seed() now explicitly marks EVERY loaded plugin (see
        // PluginToolBoundaryTrackerTests.Seed_NothingToVerify_StillMarksTheRegistryVerified) even when
        // it has nothing to verify, so "never marked" here means genuinely unseeded, not "seeded with
        // an empty boundary."
        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Pending);
    }

    [Fact]
    public void MarkBoundaryFaulted_ThenGetBoundaryStatus_ReturnsFaulted()
    {
        _sut.MarkBoundaryFaulted("azure", "DeniedTools entry 'file_wrte' matches no known tool");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Faulted);
    }

    [Fact]
    public void GetBoundaryStatus_CaseInsensitive_ReturnsFaulted()
    {
        _sut.MarkBoundaryFaulted("Azure", "reason");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Faulted);
        _sut.GetBoundaryStatus("AZURE").Should().Be(PluginBoundaryStatus.Faulted);
    }

    [Fact]
    public void MarkBoundaryPending_ThenGetBoundaryStatus_ReturnsPending()
    {
        _sut.MarkBoundaryPending("azure");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Pending);
    }

    [Fact]
    public void MarkBoundaryPending_ThenMarkBoundaryVerified_ReturnsVerified()
    {
        _sut.MarkBoundaryPending("azure");
        _sut.MarkBoundaryVerified("azure");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Verified);
    }

    [Fact]
    public void MarkBoundaryFaulted_ThenMarkBoundaryVerified_StaysFaulted()
    {
        // Faulted is terminal (#524 redesign) — a caller resolving one pending entry has no way to
        // know whether some OTHER entry already faulted this same plugin through a different call,
        // so MarkBoundaryVerified must never be able to downgrade it.
        _sut.MarkBoundaryFaulted("azure", "DeniedTools entry 'file_wrte' matches no known tool");
        _sut.MarkBoundaryVerified("azure");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Faulted);
    }

    [Fact]
    public void MarkBoundaryFaulted_ThenMarkBoundaryPending_StaysFaulted()
    {
        // Same terminal guarantee as MarkBoundaryVerified above (#524 round-2 code-review): the two
        // methods had this guard asymmetrically — only Verified refused to downgrade Faulted. Not
        // reachable via any caller today (Seed calls MarkBoundaryPending at most once per plugin, and
        // never after a fault), but the registry is the shared trust boundary, not any one caller's
        // discipline, so it must hold regardless of how many callers exist in the future.
        _sut.MarkBoundaryFaulted("azure", "DeniedTools entry 'file_wrte' matches no known tool");
        _sut.MarkBoundaryPending("azure");

        _sut.GetBoundaryStatus("azure").Should().Be(PluginBoundaryStatus.Faulted);
    }
}
