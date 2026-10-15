using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.BackgroundServices;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Learnings;
using Infrastructure.AI.Runs;
using Infrastructure.AI.WorkMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// Pins that a hot-reloaded configuration value that fails validation cannot stop the host (#821).
/// </summary>
/// <remarks>
/// A section bound with <c>ValidateFluentValidation</c> makes <c>IOptionsMonitor.CurrentValue</c> throw
/// after a reload that fails validation. A background loop that reads it outside its per-tick <c>try</c>
/// lets that exception escape <c>ExecuteAsync</c>, and <c>BackgroundServiceExceptionBehavior.StopHost</c>
/// then stops the whole host. Each test hands a service a monitor that throws and asserts the service is
/// still running afterwards. An uncontained read on the first line faults <c>StartAsync</c> itself, so the
/// assertion holds for both the synchronous and the after-a-wait cases.
/// </remarks>
public sealed class BackgroundServiceConfigResilienceTests
{
    [Fact]
    public async Task LearningsPruning_ConfigUnreadable_KeepsRunning()
    {
        var monitor = new SwitchableMonitor<LearningsConfig>(new LearningsConfig()) { Throwing = true };
        var sut = new LearningsPruningBackgroundService(
            Mock.Of<ILearningDecayService>(), monitor, NullLogger<LearningsPruningBackgroundService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task RunRecordCleanup_ConfigUnreadable_KeepsRunning()
    {
        var sut = new RunRecordCleanupService(
            Mock.Of<IRunJobStore>(),
            [],
            Mock.Of<IEscalationService>(),
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true },
            NullLogger<RunRecordCleanupService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task ParkedRunResume_ConfigUnreadable_KeepsRunning()
    {
        var sut = new ParkedRunResumeService(
            Mock.Of<IRunJobStore>(),
            Mock.Of<IRunDispatchQueue>(),
            Mock.Of<IEscalationService>(),
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true },
            NullLogger<ParkedRunResumeService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task BundleWorkspaceCleanup_ConfigUnreadable_KeepsRunning()
    {
        var sut = new BundleWorkspaceCleanupService(
            Mock.Of<IBundleHandleStore>(),
            Mock.Of<IBundleRunJobStore>(),
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true },
            NullLogger<BundleWorkspaceCleanupService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task WorkMemorySynthesis_ConfigUnreadable_KeepsRunning()
    {
        var sut = new WorkMemorySynthesisBackgroundService(
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IAmbientRequestScope>(),
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true },
            new FakeTimeProvider(),
            NullLogger<WorkMemorySynthesisBackgroundService>.Instance);

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task PeriodicSweep_ConfigUnreadable_KeepsRunning()
    {
        var sut = new ProbeSweepService(
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true }, new FakeTimeProvider());

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task PeriodicSweep_ConfigBecomesUnreadableAfterTheWait_KeepsRunning()
    {
        var time = new FakeTimeProvider();
        var monitor = new SwitchableMonitor<AppConfig>(new AppConfig());
        var sut = new ProbeSweepService(monitor, time);
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // The bad edit lands while the service is waiting out its interval; the read AFTER the wait is
        // the one that used to escape.
        monitor.Throwing = true;
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(250);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("a read after the wait must be contained too");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ScheduleDispatch_ConfigUnreadableAtTheTickRead_KeepsRunning()
    {
        var sut = CreateScheduleDispatch(
            new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true }, new FakeTimeProvider());

        await AssertKeepsRunningAsync(sut);
    }

    [Fact]
    public async Task ScheduleDispatch_ConfigBecomesUnreadableAfterTheWait_KeepsRunning()
    {
        var time = new FakeTimeProvider();
        var monitor = new SwitchableMonitor<AppConfig>(new AppConfig());
        var sut = CreateScheduleDispatch(monitor, time);
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        monitor.Throwing = true;
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(250);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("the Enabled read after the wait must be contained too");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EscalationReconciliation_ConfigBecomesUnreadableBeforeThePrune_KeepsRunning()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new AppConfig();
        config.AI.Governance.DurableState.EscalationsEnabled = true;
        var monitor = new SwitchableMonitor<AppConfig>(config);
        var reconciler = new Mock<IEscalationReconciler>();
        reconciler
            .Setup(r => r.ReconcileStuckEscalationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationReconcileResult { Recovered = [], StillStuck = [] });
        var pruner = new Mock<IGovernanceStatePruner>();
        pruner
            .Setup(p => p.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GovernanceStatePruneResult(0, 0));
        var sut = new EscalationReconciliationService(
            reconciler.Object, () => pruner.Object, monitor, time, NullLogger<EscalationReconciliationService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        // Let the first pass run on a healthy config, so the loop is demonstrably going. The clock is
        // advanced on every poll: the loop re-arms its timer only after a pass, so one advance could
        // land before the timer exists and be missed.
        await WaitForAsync(() => reconciler.Invocations.Count >= 1, () => time.Advance(TimeSpan.FromHours(1)));

        monitor.Throwing = true;
        await WaitForAsync(() => reconciler.Invocations.Count >= 2, () => time.Advance(TimeSpan.FromHours(1)));
        await Task.Delay(250);

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("the retention read before the prune must be contained");
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EscalationReconciliation_ConfigUnreadableFromConstruction_ConstructsAndKeepsRunning()
    {
        var monitor = new SwitchableMonitor<AppConfig>(new AppConfig()) { Throwing = true };
        EscalationReconciliationService? sut = null;

        // The constructor itself reads the durable-state toggles, and ExecuteAsync reads the reconcile
        // interval before its first await: both ran unguarded until the review of #821.
        var construction = Record.Exception(() => sut = new EscalationReconciliationService(
            Mock.Of<IEscalationReconciler>(), () => Mock.Of<IGovernanceStatePruner>(), monitor,
            new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<EscalationReconciliationService>.Instance));

        construction.Should().BeNull("an unreadable value must not fault host construction");
        await AssertKeepsRunningAsync(sut!);
    }

    [Fact]
    public async Task RunRecordCleanup_CeilingBecomesUnreadable_StillReclaimsExpiredRuns()
    {
        var config = new AppConfig();
        config.AI.WorkflowSubmission.RunSweepInterval = TimeSpan.FromSeconds(1);
        var monitor = new SwitchableMonitor<AppConfig>(config);
        var store = new Mock<IRunJobStore>();
        store.Setup(s => s.ExpireStaleParkedRuns(It.IsAny<TimeSpan>())).Returns([]);
        store.Setup(s => s.SweepExpired()).Returns([]);
        var sut = new RunRecordCleanupService(
            store.Object, [], Mock.Of<IEscalationService>(), monitor, NullLogger<RunRecordCleanupService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        // The ceiling is read first in each sweep. If an unreadable value aborted the tick there, run records
        // would stop being reclaimed for as long as the bad edit lasted.
        monitor.Throwing = true;
        await WaitForAsync(
            () => store.Invocations.Any(i => i.Method.Name == nameof(IRunJobStore.SweepExpired)), () => { });

        sut.ExecuteTask!.IsCompleted.Should().BeFalse();
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChangeProposal_ModeBecomesUnreadable_StillDispatchesTheProposal()
    {
        var monitor = new SwitchableMonitor<AppConfig>(new AppConfig());
        var orchestrator = new Mock<IChangeProposalOrchestrator>();
        orchestrator
            .Setup(o => o.ProcessAsync(It.IsAny<string>(), It.IsAny<OrchestratorMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChangeProposal?)null);
        var services = new ServiceCollection().AddSingleton(orchestrator.Object).BuildServiceProvider();
        var queue = new SingleItemQueue();
        var sut = new ChangeProposalBackgroundService(
            queue, services.GetRequiredService<IServiceScopeFactory>(), monitor,
            NullLogger<ChangeProposalBackgroundService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        // The mode is read once per dequeued proposal, outside the dispatch try, inside the drain loop.
        monitor.Throwing = true;
        queue.Add("p1");
        await WaitForAsync(() => orchestrator.Invocations.Count >= 1, () => { });

        sut.ExecuteTask!.IsCompleted.Should().BeFalse("an unreadable mode must not end the drain loop");
        await sut.StopAsync(CancellationToken.None);
    }

    private static ScheduleDispatchBackgroundService CreateScheduleDispatch(
        IOptionsMonitor<AppConfig> monitor, TimeProvider time) =>
        new(
            Mock.Of<IScheduleStore>(),
            Mock.Of<IRunJobStore>(),
            Mock.Of<IRunDispatchQueue>(),
            monitor,
            time,
            NullLogger<ScheduleDispatchBackgroundService>.Instance);

    private static async Task AssertKeepsRunningAsync(BackgroundService sut)
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
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            onEachPoll();
            await Task.Delay(20);
        }

        condition().Should().BeTrue("the loop should have reached this point within five seconds");
    }

    /// <summary>A dispatch queue the test can push one id into, after the service is already draining.</summary>
    private sealed class SingleItemQueue : IChangeProposalDispatchQueue
    {
        private readonly System.Threading.Channels.Channel<string> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        public void Add(string id) => _channel.Writer.TryWrite(id);

        public ValueTask EnqueueAsync(string proposalId, CancellationToken cancellationToken) =>
            _channel.Writer.WriteAsync(proposalId, cancellationToken);

        public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>A minimal sweep service whose only job is to read its retention settings.</summary>
    private sealed class ProbeSweepService(IOptionsMonitor<AppConfig> monitor, TimeProvider time)
        : PeriodicSweepService(TimeSpan.FromMinutes(1), time, NullLogger.Instance)
    {
        protected override SweepRetentionSnapshot ReadRetention()
        {
            _ = monitor.CurrentValue;
            return new SweepRetentionSnapshot(true, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        protected override string ReclaimedLogMessage => "reclaimed {Count}";

        protected override string FailureLogMessage => "failed";
    }

    /// <summary>
    /// An options monitor that hands back a fixed value, or throws the way the real one does once a
    /// reloaded section fails validation.
    /// </summary>
    private sealed class SwitchableMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public volatile bool Throwing;

        public T CurrentValue => Throwing
            ? throw new OptionsValidationException("AppConfig", typeof(T), ["a mistyped live edit"])
            : value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
