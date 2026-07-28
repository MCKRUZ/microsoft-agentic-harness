using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="EscalationReconciliationService"/> — the production trigger for
/// escalation reconciliation. Without it the recovery path has no non-test caller, so a crash
/// between the durable resolution write and the compliance audit write would strand a
/// human-granted approval permanently.
/// </summary>
public sealed class EscalationReconciliationServiceTests
{
    private readonly Mock<IEscalationReconciler> _reconciler = new();
    private readonly Mock<IGovernanceStatePruner> _pruner = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public EscalationReconciliationServiceTests()
    {
        _reconciler
            .Setup(r => r.ReconcileStuckEscalationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EscalationReconcileResult { Recovered = [], StillStuck = [] });
        _pruner
            .Setup(p => p.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GovernanceStatePruneResult(0, 0));
    }

    private EscalationReconciliationService CreateService(
        bool escalationsEnabled, int retentionDays = 90, int intervalSeconds = 300)
    {
        var config = new AppConfig();
        config.AI.Governance.DurableState.EscalationsEnabled = escalationsEnabled;
        config.AI.Governance.DurableState.RetentionDays = retentionDays;
        config.AI.Governance.DurableState.ReconcileIntervalSeconds = intervalSeconds;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);

        return new EscalationReconciliationService(
            _reconciler.Object,
            () => { _prunerResolved = true; return _pruner.Object; },
            monitor.Object,
            _time,
            NullLogger<EscalationReconciliationService>.Instance);
    }

    private bool _prunerResolved;

    [Fact]
    public async Task StartAsync_DurabilityDisabled_NeverResolvesThePruner()
    {
        // Resolving the pruner constructs the schema initializer, whose EnsureCreated creates
        // the governance-state database file. A host with durability off must never touch the
        // filesystem, so the deferred factory must stay uncalled.
        var service = CreateService(escalationsEnabled: false);

        await service.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await service.StopAsync(CancellationToken.None);

        _prunerResolved.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_DurabilityDisabled_StillReconciles()
    {
        // The in-memory stuck shape — a resolution whose fail-closed AUDIT write threw — is
        // caused by the audit store, not the state store, so it happens with durability off.
        // While the whole loop was gated on the durable toggles, nothing drove recovery in the
        // default configuration: an escalation parked on an audit outage stayed parked forever,
        // and AwaitingReconciliation's "poll until reconciliation completes" contract was a lie.
        var service = CreateService(escalationsEnabled: false);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);

        // And it keeps ticking, so a state that gets stuck later is still recovered.
        await WaitForReconcileCountAsync(2);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_DurabilityEnabled_RunsFirstPassAfterInitialDelay()
    {
        var service = CreateService(escalationsEnabled: true);

        await service.StartAsync(CancellationToken.None);

        // Before the grace period elapses, nothing has run — the host is still booting.
        _reconciler.Verify(
            r => r.ReconcileStuckEscalationsAsync(It.IsAny<CancellationToken>()), Times.Never);

        await WaitForReconcileCountAsync(1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_DurabilityEnabled_RunsRepeatedlyOnTheConfiguredInterval()
    {
        var service = CreateService(escalationsEnabled: true, intervalSeconds: 300);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);

        // The loop keeps ticking: a single pass is not enough, because the stuck records this
        // recovers can appear at any time after startup.
        await WaitForReconcileCountAsync(2);
        await WaitForReconcileCountAsync(3);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ReconcileThrows_KeepsRunningAndRetriesNextInterval()
    {
        _reconciler
            .SetupSequence(r => r.ReconcileStuckEscalationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("audit store unavailable"))
            .ReturnsAsync(new EscalationReconcileResult { Recovered = [], StillStuck = [] });

        var service = CreateService(escalationsEnabled: true);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);

        // A failing pass must never fault the host's background pipeline.
        _time.Advance(TimeSpan.FromSeconds(300));
        await WaitForReconcileCountAsync(2);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RetentionEnabled_PrunesUsingTheConfiguredWindow()
    {
        var service = CreateService(escalationsEnabled: true, retentionDays: 30);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);

        await WaitForAsync(() => _pruner.Invocations.Count > 0);
        _pruner.Verify(
            p => p.PruneAsync(
                It.Is<DateTimeOffset>(cutoff => cutoff <= _time.GetUtcNow().AddDays(-29)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RetentionDisabled_NeverPrunes()
    {
        var service = CreateService(escalationsEnabled: true, retentionDays: 0);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);
        await service.StopAsync(CancellationToken.None);

        _pruner.Verify(
            p => p.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Waits until the reconciler has been invoked at least <paramref name="expected"/> times,
    /// nudging the fake clock while it waits.
    /// </summary>
    /// <remarks>
    /// The loop under test alternates between an <c>await</c> on the reconciler and an
    /// <c>await Task.Delay(interval, timeProvider)</c>. Its continuations resume on the thread
    /// pool, so the moment a new timer is registered is not observable from the test thread —
    /// a single up-front <c>Advance</c> can land before the timer exists and then never fire
    /// it. Re-advancing on each poll makes the wait independent of that scheduling race while
    /// still proving the loop actually iterates.
    /// </remarks>
    /// <param name="expected">The minimum invocation count to wait for.</param>
    private Task WaitForReconcileCountAsync(int expected) =>
        WaitForAsync(() => ReconcileCount >= expected);

    private int ReconcileCount => _reconciler.Invocations.Count(i =>
        i.Method.Name == nameof(IEscalationReconciler.ReconcileStuckEscalationsAsync));

    private async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            _time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(10, cts.Token);
        }
    }
}
