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

    /// <summary>
    /// The configuration instance the monitor hands back on every <c>CurrentValue</c> read. Held as
    /// a field so a test can mutate it mid-run, which is exactly what an operator editing
    /// <c>appsettings.json</c> does on a host whose configuration was loaded with
    /// <c>reloadOnChange: true</c>.
    /// </summary>
    private AppConfig _config = new();

    private EscalationReconciliationService CreateService(
        bool escalationsEnabled, int retentionDays = 90, int intervalSeconds = 300,
        bool callOnceEnforcementEnabled = false)
    {
        var config = new AppConfig();
        config.AI.Governance.DurableState.EscalationsEnabled = escalationsEnabled;
        config.AI.Governance.DurableState.RetentionDays = retentionDays;
        config.AI.Governance.DurableState.ReconcileIntervalSeconds = intervalSeconds;
        config.AI.Governance.DurableState.CallOnceEnforcementEnabled = callOnceEnforcementEnabled;
        _config = config;

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
    public async Task StartAsync_DurabilityEnabledAfterConstruction_StillNeverResolvesThePruner()
    {
        // A live edit to appsettings.json must NOT bring the pruner to life, and this is the one
        // place that guarantee is enforceable. AppConfigHelper loads configuration with
        // reloadOnChange: true, so IOptionsMonitor.CurrentValue would observe the flip on the very
        // next tick. Honouring it would resolve the pruner, which constructs the schema
        // initializer, whose EnsureCreated creates the governance-state database — on a host that
        // booted with both toggles off.
        //
        // Two things break if that happens. The stores are already frozen at first resolution, so
        // the new database would sit there unwritten while retention pruned a table nothing was
        // filling. Worse, DependencyInjection.ResolveGovernanceStateProtectedPaths decided at
        // composition that there was nothing to protect and left the file-system tool's deny list
        // and hard-link check disarmed — so a database would appear behind a sandbox that had
        // already concluded it had no reason to guard that directory.
        var service = CreateService(escalationsEnabled: false);

        await service.StartAsync(CancellationToken.None);

        // Wait for a real pass before touching anything, so the loop is demonstrably running.
        // Advancing the clock and asserting immediately would prove nothing: the continuation runs
        // on the thread pool, so the assertion could win the race and pass without a single pass
        // having executed.
        await WaitForReconcileCountAsync(1);

        // The operator edits the file. Same instance the monitor hands back, so this is exactly
        // what a CurrentValue read would observe on the next tick.
        _config.AI.Governance.DurableState.ChangeProposalsEnabled = true;

        // Three, not two. A pass runs reconcile THEN prune, so observing reconcile #3 is what
        // proves prune #2 — a prune that unambiguously began after the flip — ran to completion.
        // Any weaker wait leaves room for the prune step never to have been reached at all.
        await WaitForReconcileCountAsync(3);

        await service.StopAsync(CancellationToken.None);

        _prunerResolved.Should().BeFalse(
            "the enable pair is snapshotted at construction, so durability stays off until restart");
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
    public async Task StartAsync_OnlyCallOnceEnforcementEnabled_StillResolvesThePruner()
    {
        // The exact shape of the bug this test guards against: a host that opts into ONLY
        // CallOnceEnforcementEnabled (never Escalations or ChangeProposals) still writes rows —
        // to tool_call_ledger — and must still get the retention window, not silently skip pruning
        // because the enable check only recognised the other two toggles.
        var service = CreateService(escalationsEnabled: false, callOnceEnforcementEnabled: true);

        await service.StartAsync(CancellationToken.None);
        await WaitForReconcileCountAsync(1);
        await WaitForAsync(() => _prunerResolved);
        await service.StopAsync(CancellationToken.None);

        _prunerResolved.Should().BeTrue();
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
