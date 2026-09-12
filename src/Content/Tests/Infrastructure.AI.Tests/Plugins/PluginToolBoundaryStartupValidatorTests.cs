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
            .Returns([new PluginToolBoundaryViolation("azure", PluginToolBoundaryListKind.DeniedTools, "file_wrte")]);
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
    public async Task StartAsync_PassesEveryConfiguredServerNameToSeed_IncludingDisabled()
    {
        // #613: a disabled-but-configured server is still a real, named server that could explain a
        // plugin's boundary entry the moment it's re-enabled — it is not "the same as not configured
        // at all." Seed's own existence check ("no MCP server is configured anywhere on this host")
        // must see it, or a plugin legitimately referencing a merely-disabled server gets refused at
        // boot (or, on a multi-server host, permanently denied) for no real reason. This replaces the
        // old, buggy expectation that only enabled servers were passed.
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

        captured.Should().BeEquivalentTo(["enabled-server", "disabled-server"]);
    }

    [Fact]
    public async Task StartAsync_SeedSucceeds_BackgroundProberSkipsDisabledPendingServers()
    {
        // #613: PendingServerNames can now legitimately include a disabled server (see the test
        // above) — but a disabled server can never actually connect (McpConnectionManager.CreateClientAsync
        // throws "is disabled" deterministically), so proactively probing it would only waste a
        // retry budget and log noise, and — before the McpToolProvider-level fix — would have
        // permanently faulted the plugin waiting on it. The proactive loop must never attempt one.
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        _tracker.Setup(t => t.PendingServerNames).Returns(["enabled-server", "disabled-server"]);
        _toolProvider
            .Setup(p => p.IsServerAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var queried = new ConcurrentBag<string>();
        var enabledQueried = new TaskCompletionSource();
        _toolProvider
            .Setup(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string serverName, CancellationToken _) =>
            {
                queried.Add(serverName);
                enabledQueried.TrySetResult();
                return Task.FromResult<IList<AITool>>([]);
            });
        var servers = new ConcurrentDictionary<string, McpServerDefinition>
        {
            ["enabled-server"] = new() { Enabled = true },
            ["disabled-server"] = new() { Enabled = false },
        };
        var aiConfig = new Mock<IOptionsMonitor<AIConfig>>();
        aiConfig.Setup(m => m.CurrentValue).Returns(new AIConfig { McpServers = new McpServersConfig { Servers = servers } });
        var sut = MakeSut(_ => true, aiConfig.Object);

        await sut.StartAsync(CancellationToken.None);

        await Task.WhenAny(enabledQueried.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        queried.Should().BeEquivalentTo(["enabled-server"]);
        _toolProvider.Verify(p => p.IsServerAvailableAsync("disabled-server", It.IsAny<CancellationToken>()), Times.Never);
        _toolProvider.Verify(p => p.GetToolsAsync("disabled-server", It.IsAny<CancellationToken>()), Times.Never);
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
        var sut = MakeSut(_ => true, MakeAiConfig("server-a", "server-b"));

        await sut.StartAsync(CancellationToken.None);

        // Fire-and-forget: bounded wait for the background tasks to actually run, not a blind sleep.
        await Task.WhenAny(bothQueried.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        queried.Should().BeEquivalentTo(["server-a", "server-b"]);
    }

    [Fact]
    public async Task StartAsync_SeedSucceeds_CallsGetToolsAsyncDirectlyWithoutProbingAvailabilityFirst()
    {
        // #610 round-2 code review: this used to wrap a separate IsServerAvailableAsync retry loop
        // (5 attempts, 1s apart) around the ONE real GetToolsAsync attempt, added because
        // McpConnectionManager's own connect path had no retry of its own at the time — see the git
        // history of this test (formerly *_ServerNotYetAvailable_RetriesBeforeGivingUp) for the old
        // behavior. #610 moved that retry into McpConnectionManager.CreateClientAsync itself, which
        // GetToolsAsync already goes through via GetClientAsync — a second, outer retry loop here no
        // longer adds resilience, it only multiplies worst-case connect attempts with no shared
        // budget between the two loops. Proves the new, simpler contract: GetToolsAsync is called
        // directly, exactly once, with no IsServerAvailableAsync probing beforehand.
        _registry.Setup(r => r.GetLoadedPlugins()).Returns([MakePlugin("azure")]);
        _tracker.Setup(t => t.Seed(
                It.IsAny<IReadOnlyList<LoadedPlugin>>(), It.IsAny<Func<string, bool>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([]);
        _tracker.Setup(t => t.PendingServerNames).Returns(["server-a"]);

        var getToolsCalled = new TaskCompletionSource();
        _toolProvider
            .Setup(p => p.GetToolsAsync("server-a", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                getToolsCalled.TrySetResult();
                return Task.FromResult<IList<AITool>>([]);
            });
        var sut = MakeSut(_ => true, MakeAiConfig("server-a"));

        await sut.StartAsync(CancellationToken.None);

        await Task.WhenAny(getToolsCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        getToolsCalled.Task.IsCompletedSuccessfully.Should().BeTrue(
            "GetToolsAsync must be attempted directly, without a separate availability probe first");
        _toolProvider.Verify(
            p => p.IsServerAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the outer availability-probe loop was removed (#610) — resilience now lives in McpConnectionManager's own connect retry");
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
        var sut = MakeSut(_ => true, MakeAiConfig("server-a"));

        var startTask = sut.StartAsync(CancellationToken.None);

        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(startTask, "StartAsync must not block on a proactive resolution that never completes");
    }
}
