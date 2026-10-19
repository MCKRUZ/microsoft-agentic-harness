using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Audit;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.BackgroundServices;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// The second half of #821. A hot-reloaded value that fails validation makes
/// <c>IOptionsMonitor.CurrentValue</c> throw, and these five services each read it somewhere the first
/// pass did not cover: the two manifest watchers from <see cref="FileSystemWatcher"/> callbacks (thread-pool
/// threads, where a throw escapes to the process rather than stopping one service), and the other three
/// once at startup, which faults <c>StartAsync</c> itself. The monitor here is unreadable from the first
/// read, so a read that is still unguarded throws.
/// </summary>
public sealed class RemainingBackgroundServiceConfigResilienceTests
{
    [Fact]
    public async Task AgentManifestWatcher_ConfigUnreadable_StartsAndSurvivesItsCallbacks()
    {
        var sut = new AgentManifestWatcherService(
            ThrowingMonitor(), Mock.Of<IAgentRegistryRefresher>(), new FakeTimeProvider(),
            NullLogger<AgentManifestWatcherService>.Instance);

        await AssertWatcherSurvivesAsync(sut, sut.ScheduleInvalidate, () => sut.OnWatcherError(null, new ErrorEventArgs(new IOException())));
    }

    [Fact]
    public async Task SkillManifestWatcher_ConfigUnreadable_StartsAndSurvivesItsCallbacks()
    {
        var sut = new SkillManifestWatcherService(
            ThrowingMonitor(), Mock.Of<ISkillRegistryRefresher>(), new UnsandboxedSkillFileReader(),
            new FakeTimeProvider(), NullLogger<SkillManifestWatcherService>.Instance);

        await AssertWatcherSurvivesAsync(sut, sut.ScheduleInvalidate, () => sut.OnWatcherError(null, new ErrorEventArgs(new IOException())));
    }

    [Fact]
    public async Task BundleRunDispatcher_ConfigUnreadableAtStart_KeepsRunning()
    {
        var sut = new BundleRunBackgroundService(
            new InMemoryBundleRunDispatchQueue(), Mock.Of<IBundleRunExecutor>(), ThrowingMonitor(),
            NullLogger<BundleRunBackgroundService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task RunDispatcher_ConfigUnreadableAtStart_KeepsRunning()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var sut = new RunDispatchBackgroundService(
            new InMemoryRunDispatchQueue(), Mock.Of<IRunJobStore>(), Mock.Of<IRunProgressBroker>(), scopeFactory,
            ThrowingMonitor(), new FakeTimeProvider(), NullLogger<RunDispatchBackgroundService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task AuditChainVerification_ConfigUnreadable_StartsAndRunsAPass()
    {
        var sut = new AuditChainVerificationService(
            Array.Empty<IVerifiableAuditChain>(), ThrowingMonitor(), new FakeTimeProvider(),
            NullLogger<AuditChainVerificationService>.Instance);

        // The pass reads the receipt path before it looks at any chain.
        var pass = await Record.ExceptionAsync(() => sut.VerifyAllChainsAsync(CancellationToken.None));

        pass.Should().BeNull("an unreadable receipt path must not fault a verification pass");
        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task AgentManifestWatcher_BadEditAfterAReload_KeepsTheLiveWatchers()
    {
        using var dir = new TempDirectory();
        var monitor = new ReloadableMonitor(AgentsConfigWatching(dir.Path, watch: false));
        using var sut = new AgentManifestWatcherService(
            monitor, Mock.Of<IAgentRegistryRefresher>(), new FakeTimeProvider(),
            NullLogger<AgentManifestWatcherService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        sut.WatchedPaths.Should().BeEmpty("the host started with watching off");

        // The operator turns watching on with a hot reload, then makes a bad edit. The rebuild that follows a
        // watcher error must keep working from the reload that took effect, not from the value read at start.
        // The reload has to land after the service's own startup read (the construction prime plus the read in
        // ExecuteAsync), or that read would see the reloaded value and there would be no stale value to fall back to.
        await WaitForAsync(() => monitor.Reads >= 2, () => { });
        monitor.Reload(AgentsConfigWatching(dir.Path, watch: true));
        await WaitForAsync(() => sut.WatchedPaths.Count > 0, () => { });
        sut.WatchedPaths.Should().NotBeEmpty();
        monitor.Throwing = true;
        sut.OnWatcherError(null, new ErrorEventArgs(new IOException()));

        await WaitForAsync(() => sut.WatchedPaths.Count > 0, () => { });
        sut.WatchedPaths.Should().NotBeEmpty("a bad edit must not tear down watchers the last good config asked for");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SkillManifestWatcher_BadEditAfterAReload_KeepsTheLiveWatchers()
    {
        using var dir = new TempDirectory();
        var monitor = new ReloadableMonitor(SkillsConfigWatching(dir.Path, watch: false));
        using var sut = new SkillManifestWatcherService(
            monitor, Mock.Of<ISkillRegistryRefresher>(), new UnsandboxedSkillFileReader(), new FakeTimeProvider(),
            NullLogger<SkillManifestWatcherService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        sut.WatchedPaths.Should().BeEmpty("the host started with watching off");

        // Same ordering requirement as the agent test: the startup read must precede the reload.
        await WaitForAsync(() => monitor.Reads >= 2, () => { });
        monitor.Reload(SkillsConfigWatching(dir.Path, watch: true));
        await WaitForAsync(() => sut.WatchedPaths.Count > 0, () => { });
        sut.WatchedPaths.Should().NotBeEmpty();
        monitor.Throwing = true;
        sut.OnWatcherError(null, new ErrorEventArgs(new IOException()));

        await WaitForAsync(() => sut.WatchedPaths.Count > 0, () => { });
        sut.WatchedPaths.Should().NotBeEmpty("a bad edit must not tear down watchers the last good config asked for");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AuditChainVerification_ScheduleUnreadableAtStart_PicksTheConfiguredIntervalUpOnceReadable()
    {
        // Read order: 1 prime, 2 initial delay at ExecuteAsync start; both fail, so the shipped schedule (5 minute
        // delay, 24 hour interval) is in force. Every later read succeeds and says "every 2 minutes".
        using var dir = new TempDirectory();
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Audit = new AuditConfig { VerificationInterval = TimeSpan.FromMinutes(2), ReceiptPath = dir.Path },
            },
        };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var chain = new Mock<IVerifiableAuditChain>();
        chain.SetupGet(c => c.AuditName).Returns("test-chain");
        var passes = 0;
        chain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref passes))
            .ReturnsAsync(AuditChainVerificationResult.Valid(1));
        var sut = new AuditChainVerificationService(
            [chain.Object], new ScriptedOptionsMonitor(config, 1, 2), time,
            NullLogger<AuditChainVerificationService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => Volatile.Read(ref passes) >= 1, () => time.Advance(TimeSpan.FromMinutes(5)));
        var afterFirstPass = Volatile.Read(ref passes);
        await WaitForAsync(() => Volatile.Read(ref passes) > afterFirstPass, () => time.Advance(TimeSpan.FromMinutes(2)));
        await sut.StopAsync(CancellationToken.None);

        Volatile.Read(ref passes).Should().BeGreaterThan(afterFirstPass,
            "once the configured 2 minute interval is readable the verifier must follow it, not stay on the 24 hour seed");
    }

    private static async Task AssertWatcherSurvivesAsync(
        Microsoft.Extensions.Hosting.BackgroundService sut, Action scheduleInvalidate, Action onWatcherError)
    {
        await sut.StartAsync(CancellationToken.None);

        // Both run on FileSystemWatcher callback threads in production; an exception there is not contained.
        var callbacks = Record.Exception(() =>
        {
            scheduleInvalidate();
            onWatcherError();
        });

        callbacks.Should().BeNull("a watcher callback must not throw because the live config is unreadable");
        sut.ExecuteTask!.IsCompleted.Should().BeFalse();
        await sut.StopAsync(CancellationToken.None);
    }

    private static async Task AssertKeepsRunningAsync(Microsoft.Extensions.Hosting.BackgroundService sut)
    {
        // An uncontained read before the first await faults StartAsync itself and throws here.
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(250);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse(
            "an unreadable configuration value must not end the service, because that stops the host");
        await sut.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition, Action onEachPoll)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            onEachPoll();
            await Task.Delay(20);
        }
    }

    private static AppConfig AgentsConfigWatching(string path, bool watch) => new()
    {
        AI = new AIConfig { Agents = new AgentsConfig { BasePath = path, WatchForChanges = watch } },
    };

    private static AppConfig SkillsConfigWatching(string path, bool watch) => new()
    {
        AI = new AIConfig { Skills = new SkillsConfig { BasePath = path, WatchForChanges = watch } },
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"config-resilience-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class ReloadableMonitor(AppConfig initial) : IOptionsMonitor<AppConfig>
    {
        private AppConfig _value = initial;
        private Action<AppConfig, string?>? _listener;

        private int _reads;

        public volatile bool Throwing;

        /// <summary>How many successful reads the service has made, so a test can wait for its startup read.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public AppConfig CurrentValue
        {
            get
            {
                if (Throwing)
                    throw new OptionsValidationException("AppConfig", typeof(AppConfig), ["a mistyped live edit"]);
                Interlocked.Increment(ref _reads);
                return _value;
            }
        }

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfig, string?> listener)
        {
            _listener = listener;
            return null;
        }

        /// <summary>Applies a successful hot reload: the value changes, then the listener is told, as the real monitor does.</summary>
        public void Reload(AppConfig next)
        {
            _value = next;
            _listener?.Invoke(next, null);
        }
    }

    private static ThrowingOptionsMonitor ThrowingMonitor() => new();

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue =>
            throw new OptionsValidationException("AppConfig", typeof(AppConfig), ["a mistyped live edit"]);

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
