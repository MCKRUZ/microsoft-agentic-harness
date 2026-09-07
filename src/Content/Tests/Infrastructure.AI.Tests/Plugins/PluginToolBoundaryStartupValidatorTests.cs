using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.AI;
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
    private readonly Mock<IMcpToolProvider> _toolProvider = new();

    public PluginToolBoundaryStartupValidatorTests()
    {
        // Every existing test cares only about Seed's own behavior, not proactive resolution — an
        // unconfigured IReadOnlyCollection<string> property mock returns null, and this type's
        // background-resolution foreach over it would NRE. Set here (constructor runs before every
        // test method body) so a test that itself calls Setup(t => t.PendingServerNames) still wins —
        // Moq's last-Setup-wins semantics means that must run AFTER this default, not before.
        _tracker.Setup(t => t.PendingServerNames).Returns([]);
    }

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
            aiConfig ?? MakeAiConfig(), _toolProvider.Object, NullLogger<PluginToolBoundaryStartupValidator>.Instance);

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

    [Fact]
    public async Task StartAsync_SeedSucceeds_ProactivelyQueriesEveryPendingServerInBackground()
    {
        // #524 redesign: a plugin boundary entry left Pending after Seed must not wait for the
        // running session to organically need that server — this validator queries it itself, right
        // after boot, so the entry resolves promptly instead of staying denied indefinitely.
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        _tracker.Setup(t => t.PendingServerNames).Returns(["server-a", "server-b"]);

        // #524 round-3: the availability-poll grace period this test doesn't itself exercise needs
        // IsServerAvailableAsync mocked too — an unconfigured Mock<T> method returns null for a
        // Task<bool>, and awaiting null throws, which ResolveOneServerAsync's own catch swallows
        // before ever reaching GetToolsAsync.
        _toolProvider
            .Setup(p => p.IsServerAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var queried = new ConcurrentBag<string>();
        var bothQueried = new TaskCompletionSource();
        _toolProvider
            .Setup(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string serverName, CancellationToken _) =>
            {
                queried.Add(serverName);
                if (queried.Count == 2)
                    bothQueried.TrySetResult();
                return Task.FromResult<IList<AITool>>([]);
            });
        var sut = MakeSut(_ => true);

        await sut.StartAsync(CancellationToken.None);

        // Fire-and-forget: bounded wait for the background tasks to actually run, not a blind sleep.
        await Task.WhenAny(bothQueried.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        queried.Should().BeEquivalentTo(["server-a", "server-b"]);
    }

    [Fact]
    public async Task StartAsync_SeedSucceeds_ServerNotYetAvailable_RetriesBeforeGivingUp()
    {
        // #524 round-3 code-review: a Stdio/spawned-process MCP server can genuinely take a few
        // seconds to become reachable. Without this retry, the very first proactive probe racing a
        // still-starting server would report failure and permanently fault a healthy plugin — the
        // exact regression this fix closes. Proves GetToolsAsync isn't even attempted until
        // IsServerAvailableAsync reports true.
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        _tracker.Setup(t => t.PendingServerNames).Returns(["server-a"]);

        var availabilityCalls = 0;
        var getToolsCalled = new TaskCompletionSource();
        _toolProvider
            .Setup(p => p.IsServerAvailableAsync("server-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref availabilityCalls) >= 3); // "Available" on the 3rd poll.
        _toolProvider
            .Setup(p => p.GetToolsAsync("server-a", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                getToolsCalled.TrySetResult();
                return Task.FromResult<IList<AITool>>([]);
            });
        var sut = MakeSut(_ => true);

        await sut.StartAsync(CancellationToken.None);

        await Task.WhenAny(getToolsCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        getToolsCalled.Task.IsCompletedSuccessfully.Should().BeTrue(
            "GetToolsAsync must still be attempted once the server becomes available, not abandoned after the first failed poll");
        availabilityCalls.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task StartAsync_SeedSucceeds_DoesNotAwaitPendingServerResolutionBeforeReturning()
    {
        // The proactive query must be fire-and-forget — StartAsync blocking on live third-party MCP
        // connectivity is exactly the boot-time coupling this type's own remarks say must never
        // happen. A GetToolsAsync that never completes on its own must not stop StartAsync returning.
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        _tracker.Setup(t => t.PendingServerNames).Returns(["server-a"]);
        _toolProvider
            .Setup(p => p.IsServerAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _toolProvider
            .Setup(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<IList<AITool>>().Task); // Never completes.
        var sut = MakeSut(_ => true);

        var startTask = sut.StartAsync(CancellationToken.None);

        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(startTask, "StartAsync must not block on a proactive resolution that never completes");
    }
}
