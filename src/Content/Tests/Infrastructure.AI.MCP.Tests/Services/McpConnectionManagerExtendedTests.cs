using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Bundles;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        // NullLoggerFactory.Instance, not a bare Moq mock: a Mock<ILoggerFactory> with no CreateLogger
        // setup returns null from that call, and the real MCP SDK's session-handler construction
        // (ModelContextProtocol.McpSessionHandler..ctor) calls .IsEnabled() on whatever logger it
        // gets — a NullReferenceException deep inside the SDK for any test whose connect attempt
        // reaches that far (a real, valid transport that starts successfully before failing/being
        // cancelled), unrelated to whatever behavior the test is actually trying to exercise.
        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            NullLoggerFactory.Instance,
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
    public async Task GetClientAsync_StdioServerFirstConnectAttemptFails_RetriesBeforeThrowing()
    {
        // #610: a server's FIRST connect attempt previously had no retry at all — a single failed
        // attempt (e.g. a slow cold start) threw immediately. Real (not mocked) failing connect: a
        // real, cross-platform binary that starts and exits immediately with a nonzero code before
        // ever speaking the MCP protocol — confirmed empirically this produces
        // ClientTransportClosedException (NOT McpConnectionException, NOT OperationCanceledException),
        // exactly the "transient-shaped" failure this retry exists for. Round-2 code review found an
        // empty Command is the WRONG shape for this test: it throws a raw ArgumentException from
        // StdioClientTransportOptions's own property setter, which #610's round-2 fix now explicitly
        // excludes from retry (see GetClientAsync_StdioServerWithNoCommand... below) — asserting a
        // multi-second elapsed time against that case would validate the exact bug being fixed.
        var mcpConfig = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["stdio-retry-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = OperatingSystem.IsWindows() ? "cmd" : "sh",
                    Args = OperatingSystem.IsWindows() ? ["/c", "exit 1"] : ["-c", "exit 1"],
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(mcpConfig);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => sut.GetClientAsync("stdio-retry-test");
        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
        stopwatch.Stop();

        // 2 retry delays at ~1s each between 3 attempts — bounded well below what a single
        // StartupTimeoutSeconds-driven timeout would take, so this is measuring retry delays, not
        // per-attempt hangs.
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1800),
            "a single failed attempt must not throw immediately — the connect must retry at least twice more first");
    }

    [Fact]
    public async Task GetClientAsync_StdioServerWithNoCommand_FailsFastWithoutRetrying()
    {
        // #610 round-2 code review: an empty Command is a deterministic config error (identical to a
        // missing HTTP URL) — StdioClientTransportOptions's own property setter throws a raw
        // ArgumentException for it, which must be excluded from retry the same way McpConnectionException
        // already is, or a misconfigured server burns the full ~2s retry budget on an error retrying
        // can never fix.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["stdio-no-command"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(config);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => sut.GetClientAsync("stdio-no-command");
        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.McpConnectionException>();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "a missing Command is a deterministic config error and must fail on the first attempt, not retry");
    }

    [Fact]
    public async Task ReconnectAsync_CancelledOnItsOnlyAttempt_PropagatesCancellationNotConnectionException()
    {
        // #610 round-2 code review: a genuine caller-requested cancellation must always propagate as
        // a cancellation, never get wrapped into McpConnectionException — including on the LAST
        // (here, only) attempt, which has no following Task.Delay call to re-surface it the way an
        // earlier attempt's delay incidentally would. ReconnectAsync uses retryOnFirstConnect: false
        // (a single attempt, #610's own fix — see its remarks), making attempt 1 trivially the last
        // attempt, so a pre-cancelled token exercises exactly the buggy branch directly and
        // deterministically — using GetClientAsync's 3-attempt first-connect path instead would only
        // hit this on attempt 3, which a pre-cancelled token cannot reach (Task.Delay's own
        // cancellation check on an earlier attempt would re-surface it correctly either way, masking
        // the bug this test exists to catch).
        //
        // failedClient: null! is safe here — nothing is cached for this server name yet, so
        // ReconnectAsync's ReferenceEquals(current, failedClient) check short-circuits on
        // _clients.TryGetValue returning false and never dereferences it.
        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["stdio-cancel-test"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = OperatingSystem.IsWindows() ? "powershell" : "sh",
                    Args = OperatingSystem.IsWindows()
                        ? ["-NoProfile", "-Command", "Start-Sleep -Seconds 5"]
                        : ["-c", "sleep 5"],
                    StartupTimeoutSeconds = 10
                }
            }
        };
        var sut = CreateManager(config);
        // CancelAfter, not an already-cancelled token: an already-cancelled token throws from
        // AcquireConnectionLockAsync's own SemaphoreSlim.WaitAsync(cancellationToken) before this
        // method's retry loop is ever reached at all, which would pass regardless of this fix — the
        // delay lets the lock acquire and the process spawn first, then cancels while
        // McpClient.CreateAsync is genuinely in flight (well before the 5s sleep / 10s startup
        // timeout would end the attempt on their own).
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = () => sut.ReconnectAsync("stdio-cancel-test", null!, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
