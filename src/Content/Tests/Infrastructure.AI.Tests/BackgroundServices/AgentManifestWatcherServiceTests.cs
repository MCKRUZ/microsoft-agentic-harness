using Application.AI.Common.Interfaces;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// Tests for <see cref="AgentManifestWatcherService"/>: the automatic half of issue #705's
/// no-restart agent reload. Debounce and error-handling behaviour is exercised against the
/// internal methods directly (mirroring <c>LlmRetryQueueTests</c>'s convention) so it is testable
/// without depending on real, timing-sensitive OS filesystem event delivery.
/// </summary>
public sealed class AgentManifestWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeRefresher _refresher = new();
    private readonly List<AgentManifestWatcherService> _created = [];

    public AgentManifestWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agent-watcher-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var service in _created)
            service.Dispose();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private AgentManifestWatcherService CreateService(
        bool watch = true, int debounceMs = 500, string? agentsPath = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Agents = new AgentsConfig
                {
                    BasePath = agentsPath ?? _tempDir,
                    WatchForChanges = watch,
                    ChangeDebounceMilliseconds = debounceMs,
                },
            },
        };

        var service = new AgentManifestWatcherService(
            new StaticOptionsMonitor(appConfig),
            _refresher,
            _timeProvider,
            NullLogger<AgentManifestWatcherService>.Instance);

        _created.Add(service);
        return service;
    }

    [Fact]
    public async Task ExecuteAsync_WatchForChangesDisabled_CreatesNoWatchers()
    {
        var service = CreateService(watch: false);

        await service.StartAsync(CancellationToken.None);
        service.WatchedPaths.Should().BeEmpty();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WatchForChangesEnabled_WatchesTheConfiguredExistingPath()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.WatchedPaths.Count > 0);
        service.WatchedPaths.Should().ContainSingle(p => p == _tempDir);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ConfiguredPathDoesNotExist_WatchesNothingWithoutThrowing()
    {
        var service = CreateService(agentsPath: Path.Combine(_tempDir, "does-not-exist"));

        var act = async () => await service.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        service.WatchedPaths.Should().BeEmpty();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartThenStop_DisposesWatchersCleanly()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.WatchedPaths.Count > 0);
        service.WatchedPaths.Should().NotBeEmpty();

        await service.StopAsync(CancellationToken.None);
        service.WatchedPaths.Should().BeEmpty();
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> is true or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundService.StartAsync</c> returns as soon as its <c>ExecuteAsync</c> task is
    /// merely started, not once it has actually reached its first real suspension point — so
    /// asserting on watcher state immediately after <c>await StartAsync(...)</c> races the
    /// background task. This mirrors how production code must treat the watcher becoming active:
    /// eventually, not synchronously with <c>StartAsync</c> returning.
    /// </remarks>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition did not become true within {0}ms", timeoutMs);
    }

    [Fact]
    public async Task RealWatcher_AgentFolderMovedIntoWatchedRoot_TriggersInvalidate()
    {
        // The correctness-gate finding this test closes (#705): a Filter of "AGENT.md" only
        // matches an item literally named that, so moving or renaming an entire agent folder into
        // the watched root — `mv billing-agent agents/billing-agent`, or restoring a folder from
        // the Recycle Bin — raises a single folder-level event whose Name is the folder, not
        // "AGENT.md". That event was silently dropped: the new agent stayed invisible until
        // something else triggered a reload. Uses a real FileSystemWatcher against the real
        // filesystem (unlike the debounce-logic tests above, which exercise the internal methods
        // directly) because the defect is specifically in what the OS-level watcher is configured
        // to listen for — a fake can't reproduce it. Real TimeProvider, not the class's shared fake,
        // so the debounce timer fires on its own after the real OS event arrives, rather than
        // requiring the test to guess when to advance a fake clock relative to unpredictable OS
        // event delivery timing.
        var outsideDir = Path.Combine(Path.GetTempPath(), $"agent-watcher-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "AGENT.md"), "---\nid: moved-in\nname: Moved In\n---\n");

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Agents = new AgentsConfig
                {
                    BasePath = _tempDir,
                    WatchForChanges = true,
                    ChangeDebounceMilliseconds = 50,
                },
            },
        };
        var refresher = new FakeRefresher();
        var service = new AgentManifestWatcherService(
            new StaticOptionsMonitor(appConfig), refresher, TimeProvider.System,
            NullLogger<AgentManifestWatcherService>.Instance);
        _created.Add(service);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => service.WatchedPaths.Count > 0);

            Directory.Move(outsideDir, Path.Combine(_tempDir, "moved-in"));

            await WaitUntilAsync(() => refresher.InvalidateCount > 0, timeoutMs: 5000);

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void ScheduleInvalidate_SingleEvent_InvalidatesOnlyAfterTheDebouncePeriod()
    {
        var service = CreateService(debounceMs: 500);

        service.ScheduleInvalidate();

        _timeProvider.Advance(TimeSpan.FromMilliseconds(499));
        _refresher.InvalidateCount.Should().Be(0);

        _timeProvider.Advance(TimeSpan.FromMilliseconds(2));
        _refresher.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void ScheduleInvalidate_BurstOfEventsInsideTheQuietPeriod_CollapsesToOneInvalidation()
    {
        var service = CreateService(debounceMs: 500);

        service.ScheduleInvalidate();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        service.ScheduleInvalidate(); // resets the shared timer
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        service.ScheduleInvalidate(); // resets it again
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));

        // Only 200ms have elapsed since the LAST event — still inside the 500ms quiet period.
        _refresher.InvalidateCount.Should().Be(0);

        _timeProvider.Advance(TimeSpan.FromMilliseconds(400));

        _refresher.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void ScheduleInvalidate_TwoSeparateBursts_InvalidatesOncePerBurst()
    {
        var service = CreateService(debounceMs: 500);

        service.ScheduleInvalidate();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        _refresher.InvalidateCount.Should().Be(1);

        service.ScheduleInvalidate();
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        _refresher.InvalidateCount.Should().Be(2);
    }

    [Fact]
    public void OnWatcherError_InvalidatesUnconditionally()
    {
        var service = CreateService();

        service.OnWatcherError(this, new ErrorEventArgs(new IOException("simulated buffer overflow")));

        _refresher.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void OnWatcherError_RebuildsWatchersFromCurrentConfiguration()
    {
        var service = CreateService();

        service.OnWatcherError(this, new ErrorEventArgs(new IOException("simulated buffer overflow")));

        service.WatchedPaths.Should().ContainSingle(p => p == _tempDir);
    }

    private sealed class FakeRefresher : IAgentRegistryRefresher
    {
        public int InvalidateCount { get; private set; }

        public void Invalidate() => InvalidateCount++;

        public AgentRegistryRefreshResult Refresh() =>
            throw new NotSupportedException("The watcher must only ever Invalidate, never Refresh.");
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public StaticOptionsMonitor(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
