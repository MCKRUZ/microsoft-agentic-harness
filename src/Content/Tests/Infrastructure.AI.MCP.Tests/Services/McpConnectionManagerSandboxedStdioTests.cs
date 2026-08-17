using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Coverage for the #371 routing decision in <c>McpConnectionManager.CreateTransport</c>'s stdio
/// arm: a bundle-owned stdio server must go through a sandboxed session, while a host-configured
/// (trusted, plugin-installed) stdio server must keep launching directly, completely untouched by
/// the sandbox. Uses a fake keyed <see cref="ISandboxSessionFactory"/> registered into an isolated
/// root provider (via <c>McpConnectionManagerBundleEgressSupport</c>'s override) so the routing
/// itself is provable without a real sandbox or Docker daemon.
/// </summary>
public sealed class McpConnectionManagerSandboxedStdioTests
{
    private readonly FakeSandboxSessionFactory _fakeSessionFactory = new();

    private McpConnectionManager CreateManager(McpServersConfig hostConfig, BundleOwnedMcpServerRegistry bundleOwned)
    {
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
        });

        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            hostConfig,
            bundleOwned,
            rootServices);
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_RoutesThroughTheSandboxSessionFactory()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "attacker-chosen-binary",
            StartupTimeoutSeconds = 1
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        var act = () => sut.GetClientAsync("b1:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        _fakeSessionFactory.WasCalled.Should().BeTrue(
            "a bundle-owned stdio server must start a sandboxed session, not launch the process directly");
        exception.Which.Message.Should().Contain("failed to start",
            "the failure must come from the sandbox session factory, not a host-process spawn attempt");
        exception.Which.Message.Should().NotContain("allowed programs list",
            "that message belongs to the unsandboxed process launch path this server must never reach");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_PassesCommandAndArgsThroughToTheSessionRequest()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "npx",
            Args = ["-y", "echo-mcp"],
            StartupTimeoutSeconds = 1
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.Command.Should().Be("npx");
        _fakeSessionFactory.LastRequest.ArgumentList.Should().BeEquivalentTo(["-y", "echo-mcp"]);
        _fakeSessionFactory.LastRequest.PermissionProfile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_HonorsOperatorPermissionOverrideForThisServerName()
    {
        // Before the fix, StartSandboxedStdioSessionAsync built ToolPermissionProfile as an inline
        // literal (RequiredCapabilities=None, MinimumIsolation=Container) instead of resolving it
        // through ToolPermissionProfileResolver — so a SandboxConfig.ToolOverrides entry keyed on
        // this exact bundle server name (the same mechanism that already applies to first-party
        // tool names, and that DockerContainerLaunchPreparer.ResolveImage already honors for this
        // same name via a separate override registry) had silently no effect.
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "npx",
            StartupTimeoutSeconds = 1
        });

        var overriddenSandboxConfig = new SandboxConfig
        {
            ToolOverrides = new Dictionary<string, ToolOverrideConfig>
            {
                ["b1:local-tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.example.com"] }
            }
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
            // Registered after BuildRootServices' own IOptionsMonitor<SandboxConfig> — the container
            // resolves the LAST registration for a non-keyed singleton, so this overrides it.
            services.AddSingleton<IOptionsMonitor<SandboxConfig>>(new StaticSandboxConfigMonitor(overriddenSandboxConfig));
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.PermissionProfile.DeniedHosts.Should().Contain("evil.example.com",
            "an operator's SandboxConfig.ToolOverrides entry for this bundle server name must be honored, " +
            "the same way it already is for a first-party tool name");
        _fakeSessionFactory.LastRequest.PermissionProfile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container,
            "the isolation floor must still be raised to Container even though the override didn't set one");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_PassesSeedDirectoryAndConfiguredImageToTheSessionRequest()
    {
        // #371: the two facts that make a sandboxed stdio session actually able to run the bundle's own
        // server — the seed directory tagged onto the definition at registration, and the operator's
        // configured image (never a per-tool ToolOverrides image, since a bundle's GUID-namespaced name
        // could never match one) — must both reach the sandbox session request.
        // Under BundleExecution.TempRoot below — StartSandboxedStdioSessionAsync's containment check
        // (#371, a code-review/security-review finding) refuses a seed directory outside the
        // configured staging root, so this test's own seed path must actually resolve under it.
        const string stagingRoot = @"C:\staged";
        const string seedDirectory = @"C:\staged\bundle-abc123";
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
            SandboxSeedDirectory = seedDirectory,
        });

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    TempRoot = stagingRoot,
                    StdioMcpServers = new BundleStdioMcpServersConfig { ContainerImage = "mcr.microsoft.com/node:20" },
                },
            },
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
            // Registered after BuildRootServices' own IOptionsMonitor<AppConfig> — last registration
            // wins for a non-keyed singleton, so this overrides the default (empty) AppConfig.
            services.AddSingleton<IOptionsMonitor<AppConfig>>(new StaticAppConfigMonitor(appConfig));
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.WorkspaceSeedDirectory.Should().Be(seedDirectory);
        _fakeSessionFactory.LastRequest.ContainerImage.Should().Be("mcr.microsoft.com/node:20");
    }

    [Fact]
    public async Task GetClientAsync_ConcurrentSandboxedStdioSessions_RefusesBeyondTheHostWideCap()
    {
        // Security-review finding: MaxServersPerBundle bounds one bundle's own container count, but
        // nothing bounded how many bundles could be concurrently staged — an authenticated caller
        // could otherwise pin an unbounded number of containers by staging enough bundles. This test
        // holds one session "in flight" (its factory call never completes until released) to prove a
        // SECOND concurrent attempt is refused while the cap (deliberately set to 1) is exhausted,
        // without needing a real session to ever succeed.
        var blockingFactory = new BlockingSandboxSessionFactory();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true, Type = McpServerType.Stdio, Command = "node", StartupTimeoutSeconds = 1,
        });
        bundleOwned.TryAdd("b2:local-tool", new McpServerDefinition
        {
            Enabled = true, Type = McpServerType.Stdio, Command = "node", StartupTimeoutSeconds = 1,
        });
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    StdioMcpServers = new BundleStdioMcpServersConfig
                    {
                        ContainerImage = "mcr.microsoft.com/node:20",
                        MaxConcurrentSessions = 1,
                    },
                },
            },
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, blockingFactory);
            services.AddSingleton<IOptionsMonitor<AppConfig>>(new StaticAppConfigMonitor(appConfig));
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);

        var firstCallTask = sut.GetClientAsync("b1:local-tool");
        await blockingFactory.EntrySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = () => sut.GetClientAsync("b2:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().Contain("Host-wide bundle stdio sandbox session cap");

        blockingFactory.Release.SetResult();
        await Assert.ThrowsAsync<McpConnectionException>(() => firstCallTask);
    }

    [Fact]
    public async Task GetClientAsync_ScopeFactoryThrowsDuringSessionStart_DoesNotLeakTheConcurrencySlot()
    {
        // /code-review finding (#371): the concurrency slot was claimed via Interlocked.Increment
        // BEFORE _scopeFactory.CreateAsyncScope() — which sat outside the method's try/finally —
        // so a throw during scope creation (e.g. an already-disposed root provider during host
        // shutdown) permanently pinned that slot for the rest of the process's life. Proven here by
        // a SECOND attempt after the first throws: if the slot leaked, it fails with "Host-wide
        // bundle stdio sandbox session cap"; if released correctly, it fails at scope creation
        // again instead, exactly like the first attempt.
        var throwingScopeFactory = new ThrowingServiceScopeFactory();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true, Type = McpServerType.Stdio, Command = "node", StartupTimeoutSeconds = 1,
        });
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    StdioMcpServers = new BundleStdioMcpServersConfig
                    {
                        ContainerImage = "mcr.microsoft.com/node:20",
                        MaxConcurrentSessions = 1,
                    },
                },
            },
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddSingleton<IOptionsMonitor<AppConfig>>(new StaticAppConfigMonitor(appConfig));
        });
        var sut = new McpConnectionManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned,
            throwingScopeFactory, McpConnectionManagerBundleEgressSupport.Args.AmbientScope, rootServices);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));
        var secondException = await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        secondException.Message.Should().NotContain("Host-wide bundle stdio sandbox session cap");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_SeedDirectoryOutsideStagingRoot_RefusesWithoutStartingASession()
    {
        // Security-review finding: SandboxSeedDirectory is provenance-tagged by BundleStagingService
        // today, but the field itself is just a public string with no structural guarantee. This test
        // proves the containment check is real, not just documented convention — a seed directory
        // that resolves outside the configured staging root must never reach the sandbox session
        // factory at all.
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
            SandboxSeedDirectory = @"C:\definitely-not-the-staging-root\evil",
        });

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    TempRoot = @"C:\staged",
                    StdioMcpServers = new BundleStdioMcpServersConfig { ContainerImage = "mcr.microsoft.com/node:20" },
                },
            },
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
            services.AddSingleton<IOptionsMonitor<AppConfig>>(new StaticAppConfigMonitor(appConfig));
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);

        var act = () => sut.GetClientAsync("b1:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().Contain("outside the configured bundle staging root");
        _fakeSessionFactory.WasCalled.Should().BeFalse(
            "an out-of-bounds seed directory must be refused before the sandbox session factory is ever called");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_NoConfiguredImage_LeavesRequestImageNull()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
        });
        // BuildRootServices' default AppConfig has an empty (unconfigured) StdioMcpServers.ContainerImage.
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.ContainerImage.Should().BeNull(
            "an unconfigured image must fall through to the session factory's own default resolution, " +
            "not an empty string that would fail Docker's image reference parsing");
    }

    [Fact]
    public async Task GetClientAsync_HostConfiguredStdioServer_NeverReachesTheSandbox()
    {
        // The trust-tier boundary #371 must not blur: a host-installed plugin's stdio server keeps
        // launching directly on the host, exactly as before this feature existed.
        var hostConfig = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["plugin:local-tool"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "nonexistent-binary",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(hostConfig, new BundleOwnedMcpServerRegistry());

        var act = () => sut.GetClientAsync("plugin:local-tool");

        await act.Should().ThrowAsync<McpConnectionException>();
        _fakeSessionFactory.WasCalled.Should().BeFalse(
            "a host-configured server is a different trust tier and must never route through the sandbox");
    }

    private sealed class StaticAppConfigMonitor(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => value;
        public AppConfig Get(string? name) => value;
        public IDisposable OnChange(Action<AppConfig, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class StaticSandboxConfigMonitor(SandboxConfig value) : IOptionsMonitor<SandboxConfig>
    {
        public SandboxConfig CurrentValue => value;
        public SandboxConfig Get(string? name) => value;
        public IDisposable OnChange(Action<SandboxConfig, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A session factory whose <see cref="StartSessionAsync"/> does not complete until the test
    /// explicitly releases it — lets a test hold a "slot" open to prove the host-wide concurrency cap
    /// rejects a second concurrent attempt while it's exhausted, without needing a real session.
    /// </summary>
    private sealed class BlockingSandboxSessionFactory : ISandboxSessionFactory
    {
        public TaskCompletionSource EntrySignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
        {
            EntrySignaled.TrySetResult();
            await Release.Task;
            return Result<ISandboxSession>.Fail("fake factory: not starting a real session");
        }
    }

    /// <summary>Its <see cref="CreateScope"/> always throws — stands in for an already-disposed root provider during host shutdown.</summary>
    private sealed class ThrowingServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("test: scope factory intentionally throws");
    }

    private sealed class FakeSandboxSessionFactory : ISandboxSessionFactory
    {
        public bool WasCalled { get; private set; }
        public SandboxSessionRequest? LastRequest { get; private set; }

        public Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
        {
            WasCalled = true;
            LastRequest = request;
            return Task.FromResult(Result<ISandboxSession>.Fail("fake factory: not starting a real session"));
        }
    }
}
