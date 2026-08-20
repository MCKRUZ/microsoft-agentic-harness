using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Bundles;
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
using ModelContextProtocol.Client;
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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

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
                ["b1:local-tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.PermissionProfile.DeniedCapabilities.Should().Be(ToolCapability.NetworkAccess,
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
        // Built via Path.Combine (never a hardcoded "C:\..." literal): a Windows-style absolute path
        // is not rooted at all on Linux, so PathScope.IsSameOrUnder's Path.GetFullPath-based
        // normalization would treat the whole literal as one opaque path segment relative to the
        // working directory — the seed would never resolve as "under" the staging root there, and
        // this positive-containment test would fail in Linux CI while passing on a Windows dev
        // machine. Caught when this exact thing happened on this PR's first CI run.
        var stagingRoot = Path.Combine(Path.GetTempPath(), "staged");
        var seedDirectory = Path.Combine(stagingRoot, "bundle-abc123");
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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

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
        // factory at all. Sibling of (never nested under) the staging root, both built via
        // Path.Combine so the "outside" relationship holds on any OS — see the positive-containment
        // test above for why a hardcoded "C:\..." literal doesn't portably mean what it looks like.
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
            SandboxSeedDirectory = Path.Combine(Path.GetTempPath(), "definitely-not-the-staging-root", "evil"),
        });

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    TempRoot = Path.Combine(Path.GetTempPath(), "staged"),
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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        var act = () => sut.GetClientAsync("b1:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().Contain("outside the configured bundle staging root");
        _fakeSessionFactory.WasCalled.Should().BeFalse(
            "an out-of-bounds seed directory must be refused before the sandbox session factory is ever called");
    }

    [Fact]
    public async Task GetClientAsync_ConcurrentSandboxedStdioSessions_RefusesBeyondTheHostWideCap_WritesOneAuditRecord()
    {
        // #431: the host-wide session-cap refusal above (RefusesBeyondTheHostWideCap) previously left
        // no audit trail at all — an authenticated caller repeatedly hitting the cap was invisible to
        // the governance audit chain. Same scenario as that test, plus a mocked IGovernanceAuditService
        // to prove the refusal is now recorded exactly once.
        var auditService = new Mock<IGovernanceAuditService>();
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
            services.AddSingleton(auditService.Object);
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        var firstCallTask = sut.GetClientAsync("b1:local-tool");
        await blockingFactory.EntrySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b2:local-tool"));

        auditService.Verify(a => a.Log(It.IsAny<string>(), "b2:local-tool",
                $"host_session_cap_exceeded:{appConfig.AI.BundleExecution.StdioMcpServers.MaxConcurrentSessions}"),
            Times.Once, "the cap refusal must leave exactly one durable audit record naming the refused server");

        blockingFactory.Release.SetResult();
        await Assert.ThrowsAsync<McpConnectionException>(() => firstCallTask);
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_SeedDirectoryOutsideStagingRoot_WritesOneAuditRecord()
    {
        // #431: same containment scenario as SeedDirectoryOutsideStagingRoot_RefusesWithoutStartingASession
        // above, plus a mocked IGovernanceAuditService to prove the refusal is now recorded exactly once.
        var auditService = new Mock<IGovernanceAuditService>();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
            SandboxSeedDirectory = Path.Combine(Path.GetTempPath(), "definitely-not-the-staging-root", "evil"),
        });

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig
                {
                    TempRoot = Path.Combine(Path.GetTempPath(), "staged"),
                    StdioMcpServers = new BundleStdioMcpServersConfig { ContainerImage = "mcr.microsoft.com/node:20" },
                },
            },
        };
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
            services.AddSingleton<IOptionsMonitor<AppConfig>>(new StaticAppConfigMonitor(appConfig));
            services.AddSingleton(auditService.Object);
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        auditService.Verify(a => a.Log(It.IsAny<string>(), "b1:local-tool", "seed_outside_staging_root"), Times.Once,
            "the seed-containment refusal must leave exactly one durable audit record naming the refused server");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_SessionFactoryFails_WritesOneAuditRecord()
    {
        // #431: StartSandboxedStdioSessionAsync's third refusal branch — the sandbox session factory
        // itself returning a failed Result — previously left no audit trail. FakeSandboxSessionFactory
        // always fails (see its own doc comment), so any successful connection attempt against it
        // exercises exactly this branch.
        var auditService = new Mock<IGovernanceAuditService>();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
        });
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
            services.AddSingleton(auditService.Object);
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        auditService.Verify(a => a.Log(It.IsAny<string>(), "b1:local-tool",
                It.Is<string>(d => d.StartsWith("session_factory_failed:", StringComparison.Ordinal))),
            Times.Once, "the session-factory failure must leave exactly one durable audit record naming the refused server");
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
        using var runScope = BundleRunIdAccessor.Begin("run-1");

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.ContainerImage.Should().BeNull(
            "an unconfigured image must fall through to the session factory's own default resolution, " +
            "not an empty string that would fail Docker's image reference parsing");
    }

    // -- Per-run session isolation (#455) --

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_NoAmbientRunId_RefusesWithoutStartingASession()
    {
        // The MCP SDK's own docs: a stdio session is unsuitable for sharing across concurrent callers.
        // Resolving one with no ambient run id armed (BundleRunIdAccessor.Current is null) must fail
        // closed rather than silently falling back to the old shared-by-server-name cache — the exact
        // bug #455 exists to close. No BundleRunIdAccessor.Begin scope is opened anywhere in this test.
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "node",
            StartupTimeoutSeconds = 1,
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        var act = () => sut.GetClientAsync("b1:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().Contain("no ambient bundle run id");
        _fakeSessionFactory.WasCalled.Should().BeFalse(
            "a missing run id must be refused before the sandbox session factory is ever called — " +
            "falling back to a shared session is the bug this check exists to close");
    }

    [Fact]
    public async Task GetClientAsync_TwoRunsResolvingTheSameServerName_DoNotSerializeOnOneSharedLock()
    {
        // Distinguishes the run-scoped lock key from the old bare-server-name one. Before #455, every
        // caller resolving one server name funneled through a single SemaphoreSlim(1,1), so a second
        // run's attempt would have blocked behind the first's in-flight (never-completing, here)
        // session start. With the run id folded into the lock key, two DIFFERENT runs resolving the
        // SAME server name must proceed independently — reaching (and each failing against) the
        // factory on its own, not queuing behind the other.
        var blockingFactory = new BlockingSandboxSessionFactory();
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true, Type = McpServerType.Stdio, Command = "node", StartupTimeoutSeconds = 1,
        });
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, blockingFactory);
        });
        var sut = McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(), new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(), new McpServersConfig(), bundleOwned, rootServices);

        Task<McpClient> firstCallTask;
        using (BundleRunIdAccessor.Begin("run-1"))
            firstCallTask = sut.GetClientAsync("b1:local-tool");
        await blockingFactory.EntrySignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // If run-2 were still funneled through run-1's lock (the pre-#455 behavior), this call would
        // block waiting for that lock rather than ever reaching the factory at all — so
        // PeakConcurrentEntries would never rise above 1, however long this test waited. Polling
        // rather than a single fixed delay avoids a flaky race against exactly how fast run-2's
        // attempt reaches the factory.
        Task<McpClient> secondCallTask;
        using (BundleRunIdAccessor.Begin("run-2"))
            secondCallTask = sut.GetClientAsync("b1:local-tool");

        for (var attempt = 0; attempt < 200 && blockingFactory.PeakConcurrentEntries < 2; attempt++)
            await Task.Delay(10);

        blockingFactory.PeakConcurrentEntries.Should().Be(2,
            "both runs must be inside the sandbox session factory at once — a run-scoped lock key " +
            "must not serialize two different runs resolving the same server name");

        blockingFactory.Release.SetResult();
        await Assert.ThrowsAsync<McpConnectionException>(() => firstCallTask);
        await Assert.ThrowsAsync<McpConnectionException>(() => secondCallTask);
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedRemoteServer_NeverRequiresAnAmbientRunId()
    {
        // The run-scoping requirement is deliberately stdio-only: an http/sse transport has no
        // single-session constraint (multiple independent requests to a remote endpoint are fine), so
        // a bundle-owned REMOTE server must never hit the "no ambient run id" refusal a stdio one does.
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:remote-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Http,
            Url = "http://localhost:19999/mcp", // no listener — fast connection-refused, not the run-id check
            StartupTimeoutSeconds = 1,
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        var act = () => sut.GetClientAsync("b1:remote-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        exception.Which.Message.Should().NotContain("no ambient bundle run id");
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
    /// rejects a second concurrent attempt while it's exhausted, without needing a real session. Also
    /// tracks how many calls are inside <see cref="StartSessionAsync"/> at once (<see cref="PeakConcurrentEntries"/>),
    /// which #455's non-serialization test uses to prove two callers actually overlapped rather than
    /// queued one after the other.
    /// </summary>
    private sealed class BlockingSandboxSessionFactory : ISandboxSessionFactory
    {
        public TaskCompletionSource EntrySignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _concurrentEntries;
        private int _peakConcurrentEntries;
        public int PeakConcurrentEntries => Volatile.Read(ref _peakConcurrentEntries);

        public async Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _concurrentEntries);
            InterlockedMax(ref _peakConcurrentEntries, now);
            EntrySignaled.TrySetResult();
            try
            {
                await Release.Task;
                return Result<ISandboxSession>.Fail("fake factory: not starting a real session");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentEntries);
            }
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < candidate
                   && Interlocked.CompareExchange(ref target, candidate, seen) != seen)
            {
                // Another thread moved the peak while we were deciding; re-read and try again.
            }
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
