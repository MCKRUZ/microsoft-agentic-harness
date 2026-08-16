using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Bundles;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Extended tests for <see cref="McpConnectionManager"/> covering dispose behavior,
/// disconnect operations, transport creation edge cases, and concurrent access patterns.
/// </summary>
public sealed class McpConnectionManagerExtendedTests
{
    private static McpConnectionManager CreateManager(
        McpServersConfig? config = null, BundleOwnedMcpServerRegistry? bundleOwned = null)
    {
        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            config ?? new McpServersConfig(),
            bundleOwned ?? new BundleOwnedMcpServerRegistry());
    }

    // -- GetClientAsync after dispose --

    [Fact]
    public async Task GetClientAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateManager();
        await sut.DisposeAsync();

        var act = () => sut.GetClientAsync("any-server");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // -- DisconnectAsync --

    [Fact]
    public async Task DisconnectAsync_NonexistentServer_DoesNotThrow()
    {
        var sut = CreateManager();

        var act = async () => await sut.DisconnectAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    // -- DisconnectAsync vs. an in-flight connection lock (#378) --------------------------------------
    //
    // GetClientAsync/CreateClientAsync/ReconnectAsync all take a per-server SemaphoreSlim before
    // touching that server's cached client. Simulating "a connect attempt is in flight" by holding that
    // same semaphore directly (via reflection on the private _connectionLocks field) exercises
    // DisconnectAsync's own lock-acquisition path without needing a real, controllable MCP transport.
    // Two deterministic scenarios, not a scheduler-luck race: a hold shorter than the timeout (disconnect
    // must wait for and use the lock) and a hold longer than it (disconnect must proceed anyway, bounded,
    // never indefinitely). Both are pinned by elapsed-time bounds, not by which of two outcomes "wins."

    [Fact]
    public async Task DisconnectAsync_ConnectionLockHeldBriefly_WaitsForItRatherThanRacingPast()
    {
        // Regression test for #378: before this fix, DisconnectAsync never took the lock at all, so it
        // could interleave its eviction with a concurrent connect's cache write in either order. Holding
        // the lock for a short, bounded duration and asserting DisconnectAsync's elapsed time is AT LEAST
        // that duration proves it genuinely waited for the lock rather than proceeding immediately.
        var sut = CreateManager();
        var connectionLocks = GetConnectionLocks(sut);
        var heldLock = connectionLocks.GetOrAdd("brief-hold", _ => new SemaphoreSlim(1, 1));
        await heldLock.WaitAsync();

        var holdDuration = TimeSpan.FromMilliseconds(300);
        _ = Task.Delay(holdDuration).ContinueWith(_ => heldLock.Release());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.DisconnectAsync("brief-hold");
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(
            holdDuration - TimeSpan.FromMilliseconds(50),
            "DisconnectAsync must actually wait for the lock, not race past a holder that releases quickly");
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2), "a brief hold must resolve well before the multi-second fallback timeout");
    }

    [Fact]
    public async Task DisconnectAsync_ConnectionLockHeldLongerThanTimeout_ProceedsWithoutHangingForever()
    {
        // The other half of #378: a genuinely hung connect attempt (the lock never releases within the
        // fallback window) must not block bundle teardown indefinitely. DisconnectAsync's caller has no
        // cancellation token of its own, so the ONLY thing that can bound this wait is the fixed timeout
        // baked into DisconnectAsync itself — this proves that bound is real, not just documented.
        var sut = CreateManager();
        var connectionLocks = GetConnectionLocks(sut);
        var heldLock = connectionLocks.GetOrAdd("stuck-server", _ => new SemaphoreSlim(1, 1));
        await heldLock.WaitAsync();
        // Deliberately never released within this test — simulates a hung connect attempt.

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.DisconnectAsync("stuck-server");
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "disconnect must proceed once the fallback timeout elapses, never wait for a lock that never frees");
    }

    private static ConcurrentDictionary<string, SemaphoreSlim> GetConnectionLocks(McpConnectionManager manager)
    {
        var field = typeof(McpConnectionManager)
            .GetField("_connectionLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (ConcurrentDictionary<string, SemaphoreSlim>)field!.GetValue(manager)!;
    }

    // -- GetConfiguredServerNames edge cases --

    [Fact]
    public void GetConfiguredServerNames_AllDisabled_ReturnsEmpty()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["a"] = new() { Enabled = false },
                ["b"] = new() { Enabled = false }
            }
        };
        var sut = CreateManager(config);

        sut.GetConfiguredServerNames().Should().BeEmpty();
    }

    [Fact]
    public void GetConfiguredServerNames_MixedEnabled_ReturnsOnlyEnabled()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["enabled-1"] = new() { Enabled = true },
                ["disabled-1"] = new() { Enabled = false },
                ["enabled-2"] = new() { Enabled = true },
                ["disabled-2"] = new() { Enabled = false },
                ["enabled-3"] = new() { Enabled = true }
            }
        };
        var sut = CreateManager(config);

        var names = sut.GetConfiguredServerNames().ToList();

        names.Should().HaveCount(3);
        names.Should().BeEquivalentTo("enabled-1", "enabled-2", "enabled-3");
    }

    // -- IsConnected --

    [Fact]
    public void IsConnected_EmptyConfig_ReturnsFalse()
    {
        var sut = CreateManager();

        sut.IsConnected("anything").Should().BeFalse();
    }

    // -- GetClientAsync with invalid server config --

    [Fact]
    public async Task GetClientAsync_StdioServerWithNoCommand_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["stdio-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("stdio-test");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }

    [Fact]
    public async Task GetClientAsync_HttpServerWithNoUrl_ThrowsMcpConnectionException()
    {
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["http-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = null,
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var act = () => sut.GetClientAsync("http-test");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
    }

    // -- DisposeAsync with connection locks --

    [Fact]
    public async Task DisposeAsync_AfterMultipleGetAttempts_CleansUpLocks()
    {
        var sut = CreateManager();

        // Try to get a non-existent server (will throw), then dispose
        try { await sut.GetClientAsync("a"); } catch { }
        try { await sut.GetClientAsync("b"); } catch { }

        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
