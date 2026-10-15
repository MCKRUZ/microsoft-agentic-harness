using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// A sweep that deletes or expires things seeds its last-known-good setting to "off" (#821): when a value was
/// never read successfully, the shipped default would shorten whatever longer window the operator actually
/// configured. The seed is invisible until a setting's first prime read AND the read that follows both fail,
/// so these tests script the monitor to fail exactly those two reads (see <see cref="ScriptedOptionsMonitor"/>).
/// </summary>
public sealed class DeletingSweepSeedTests
{
    [Fact]
    public async Task EscalationReconciliation_RetentionNeverRead_PrunesNothingUntilItReadsOnce()
    {
        // Read order: 1 retention prime, 2 reconcile-interval prime, 3-4 durable-state toggle (prime + read),
        // 5 interval at ExecuteAsync start, 6 retention at the first pass's prune. Fail 1 and 6: the retention
        // setting has never read successfully when the first prune asks for it.
        var config = new AppConfig();
        config.AI.Governance.DurableState.EscalationsEnabled = true;
        var monitor = new ScriptedOptionsMonitor(config, 1, 6);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reconciler = new Mock<IEscalationReconciler>();
        reconciler
            .Setup(r => r.ReconcileStuckEscalationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationReconcileResult { Recovered = [], StillStuck = [] });
        int? reconcilePassesAtFirstPrune = null;
        var pruner = new Mock<IGovernanceStatePruner>();
        pruner
            .Setup(p => p.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback(() => reconcilePassesAtFirstPrune ??= reconciler.Invocations.Count)
            .ReturnsAsync(new GovernanceStatePruneResult(0, 0));
        var sut = new EscalationReconciliationService(
            reconciler.Object, () => pruner.Object, monitor, time, NullLogger<EscalationReconciliationService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => reconcilePassesAtFirstPrune is not null, () => time.Advance(TimeSpan.FromHours(1)));
        await sut.StopAsync(CancellationToken.None);

        reconcilePassesAtFirstPrune.Should().BeGreaterThanOrEqualTo(2,
            "the first pass's retention was unreadable, so it must have seeded 'off' and pruned nothing");
    }

    [Fact]
    public async Task RunRecordCleanup_ParkedCeilingNeverRead_ExpiresNothingOnTheFirstSweep()
    {
        // Read order: 1 sweep-interval prime, 2 parked-ceiling prime, 3 interval at the top of the loop,
        // 4 ceiling at the first sweep. Fail 2 and 4: the ceiling has never read successfully when the first
        // sweep asks for it, and the interval (read 3) is readable so the sweep runs on the test's 1s cadence.
        var config = new AppConfig();
        config.AI.WorkflowSubmission.RunSweepInterval = TimeSpan.FromSeconds(1);
        var monitor = new ScriptedOptionsMonitor(config, 2, 4);
        var ceilings = new List<TimeSpan>();
        var store = new Mock<IRunJobStore>();
        store.Setup(s => s.ExpireStaleParkedRuns(It.IsAny<TimeSpan>()))
            .Callback<TimeSpan>(ceilings.Add)
            .Returns([]);
        store.Setup(s => s.SweepExpired()).Returns([]);
        var sut = new RunRecordCleanupService(
            store.Object, [], Mock.Of<IEscalationService>(), monitor, NullLogger<RunRecordCleanupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await WaitForAsync(() => ceilings.Count >= 1, () => { });
        await sut.StopAsync(CancellationToken.None);

        ceilings.Should().NotBeEmpty();
        ceilings[0].Should().Be(TimeSpan.Zero,
            "a ceiling that was never read must switch expiry off, not fall back to the shipped seven days");
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
}
