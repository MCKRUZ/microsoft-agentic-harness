using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Runs escalation reconciliation in production: one pass shortly after startup, then on a
/// bounded interval, plus a retention prune of terminal governance-state rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this service must exist.</b> Without a scheduled trigger,
/// <see cref="IEscalationReconciler.ReconcileStuckEscalationsAsync"/> would only ever run if a
/// human happened to invoke it. The crash window it exists for — a host that dies between the
/// durable resolution write and the compliance audit write — leaves a
/// <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> row that rehydration
/// deliberately does <em>not</em> restore to the active set. That escalation would then be
/// invisible to pending-list queries, would answer
/// <c>UnknownEscalation</c> to any decision, and would return null from
/// <c>GetOutcomeAsync</c> forever: a human-granted approval permanently stranded, with the
/// plan executor's resume path polling an outcome that will never appear.
/// </para>
/// <para>
/// <b>Ordering.</b> Registered after <see cref="EscalationStateRehydrationService"/> so the
/// active set is populated before the first pass runs. Reconciliation dedupes its two stuck
/// shapes by checking the active set, so running it against an unpopulated set would treat
/// in-memory-recoverable records as durable-only ones and finalize them without their
/// in-memory state. The initial delay additionally lets a host finish booting before the first
/// scan touches the database.
/// </para>
/// <para>
/// <b>The loop is NOT gated on the durability toggles.</b> The in-memory stuck shape — a
/// resolution reached in this process whose fail-closed <em>audit</em> write threw — is caused
/// by the audit store, not the state store, so it occurs in the default (durability-off)
/// configuration too. Gating the whole service on <c>EscalationsEnabled</c> left that shape
/// with no scheduled recovery on the very configuration most hosts run, which in turn made
/// <c>EscalationDecisionStatus.AwaitingReconciliation</c>'s own contract ("the verdict
/// becomes observable once reconciliation completes") false. Each sub-step therefore checks
/// only the flag it actually needs: the reconcile pass runs always, and the retention prune —
/// which does touch the database — stays behind the construction-time snapshot of the toggles.
/// With durability off the pass's durable half is a no-op anyway, because
/// <c>NullEscalationStateStore.GetActiveAsync</c> returns nothing.
/// </para>
/// <para>
/// A pass that throws is logged and retried on the next tick — reconciliation failing must
/// never take the host down.
/// </para>
/// </remarks>
public sealed class EscalationReconciliationService : BackgroundService
{
    /// <summary>Grace period before the first pass, letting the host finish booting.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>Floor on the configured interval; the scan is cheap but not a hot loop.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly IEscalationReconciler _reconciler;
    private readonly Func<IGovernanceStatePruner> _prunerFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EscalationReconciliationService> _logger;

    /// <summary>
    /// Whether either durability toggle was on when this service was constructed. Snapshotted
    /// rather than re-read per tick — see the constructor's <c>config</c> parameter for why.
    /// </summary>
    private readonly bool _durableStateEnabled;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="reconciler">The reconciler to drive (the escalation service itself).</param>
    /// <param name="prunerFactory">
    /// Deferred accessor for the retention pruner. Deliberately <b>not</b> a direct dependency:
    /// hosted services are constructed when the host is built, and constructing the pruner pulls
    /// in the schema initializer, whose constructor runs <c>EnsureCreated</c> — which would
    /// create the governance-state database file on every host, including the ones that never
    /// enable durability. Resolving it only after the enable check preserves the
    /// "zero filesystem side effects when off" guarantee.
    /// </param>
    /// <param name="config">
    /// Supplies the enable flags, interval, and retention window. The interval and retention window
    /// are read live, because both are safe to retune on a running host. The <b>enable</b> pair is
    /// snapshotted here instead, for two reasons. First, it makes the restart-required contract
    /// documented on <c>GovernanceDurableStateConfig</c> actually true: the store selections are
    /// already frozen at first resolution, so a pruner that honoured a live edit could prune a
    /// database the stores were not writing, or — in the other direction — stop pruning while the
    /// frozen stores kept writing, letting retention lapse silently. Second, it is what keeps
    /// <c>DependencyInjection.ResolveGovernanceStateProtectedPaths</c> sound: that gate decides at
    /// composition whether the governance-state directory is worth protecting, and resolving the
    /// pruner is the one remaining route that could create that directory later in the process. A
    /// live toggle edit reaching this method would create the database on a host whose file-system
    /// deny list booted disarmed.
    /// </param>
    /// <param name="timeProvider">Clock used for the retention cutoff.</param>
    /// <param name="logger">Structured logger.</param>
    public EscalationReconciliationService(
        IEscalationReconciler reconciler,
        Func<IGovernanceStatePruner> prunerFactory,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<EscalationReconciliationService> logger)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(prunerFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _reconciler = reconciler;
        _prunerFactory = prunerFactory;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;

        var durable = config.CurrentValue.AI.Governance.DurableState;
        _durableStateEnabled =
            durable.EscalationsEnabled || durable.ChangeProposalsEnabled || durable.CallOnceEnforcementEnabled;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Deliberately unconditional (see the class remarks): the in-memory stuck shape this
        // recovers is produced by an audit-store failure and exists with durability off. The
        // per-sub-step flag checks live in RunPassAsync.
        var interval = ResolveInterval(
            _config.CurrentValue.AI.Governance.DurableState.ReconcileIntervalSeconds);
        _logger.LogInformation(
            "Escalation reconciliation active: first pass in {InitialDelay}, then every {Interval}",
            InitialDelay, interval);

        try
        {
            await Task.Delay(InitialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one reconcile pass followed by one retention prune. Each step swallows its own
    /// failures so a transient store or audit outage never faults the host's background pipeline,
    /// and so a failing prune cannot suppress the next reconcile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunPassAsync(CancellationToken ct)
    {
        await RunReconcileAsync(ct);
        await RunPruneAsync(ct);
    }

    /// <summary>
    /// Drives one reconcile pass. Runs on every host regardless of the durability toggles: the
    /// in-memory stuck shape it recovers comes from a failed audit write, which is independent
    /// of durable state. With durability off the pass's durable half self-no-ops, because the
    /// Null state store reports no active records.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunReconcileAsync(CancellationToken ct)
    {
        try
        {
            var result = await _reconciler.ReconcileStuckEscalationsAsync(ct);
            if (result.Recovered.Count > 0 || result.StillStuck.Count > 0)
            {
                _logger.LogInformation(
                    "Scheduled reconcile recovered {RecoveredCount} escalation(s); {StuckCount} still stuck",
                    result.Recovered.Count, result.StillStuck.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled escalation reconcile pass failed; retrying on the next interval");
        }
    }

    /// <summary>
    /// Prunes terminal governance-state rows past the retention window. Gated on ANY of the three
    /// durable toggles — retention applies to the change-proposal and tool-call-ledger tables too,
    /// so a host that enables only <c>ChangeProposalsEnabled</c> or only
    /// <c>CallOnceEnforcementEnabled</c> must still get the documented window — and skipped
    /// entirely when all three were off at construction, which is what keeps the deferred pruner
    /// factory (and with it the schema initializer that would create the database file) unresolved
    /// on such hosts. The enable check reads the construction-time snapshot, never
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>: see the constructor's <c>config</c>
    /// parameter for why a live re-read would be unsound. The retention window itself stays live,
    /// because retuning it on a running host changes only how far back this prune reaches.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunPruneAsync(CancellationToken ct)
    {
        if (!_durableStateEnabled)
            return;

        var retentionDays = _config.CurrentValue.AI.Governance.DurableState.RetentionDays;
        if (retentionDays <= 0)
            return;

        try
        {
            await _prunerFactory().PruneAsync(
                _timeProvider.GetUtcNow().AddDays(-retentionDays), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Governance-state retention prune failed; retrying on the next interval");
        }
    }

    /// <summary>Clamps the configured interval up to <see cref="MinimumInterval"/>.</summary>
    /// <param name="configuredSeconds">The configured interval in seconds.</param>
    private static TimeSpan ResolveInterval(int configuredSeconds)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(1, configuredSeconds));
        return configured < MinimumInterval ? MinimumInterval : configured;
    }
}
