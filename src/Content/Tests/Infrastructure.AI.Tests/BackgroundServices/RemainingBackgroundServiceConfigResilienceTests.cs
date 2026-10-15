using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Runs;
using Domain.Common.Config;
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

    private static ThrowingOptionsMonitor ThrowingMonitor() => new();

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue =>
            throw new OptionsValidationException("AppConfig", typeof(AppConfig), ["a mistyped live edit"]);

        public AppConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
