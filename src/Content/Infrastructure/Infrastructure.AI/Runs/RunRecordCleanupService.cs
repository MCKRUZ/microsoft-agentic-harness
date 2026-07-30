using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Reclaims run records whose retention window has elapsed.
/// </summary>
/// <remarks>
/// <para>
/// Without this the configured retention is a claim the host never honours: every finished run stays
/// held for the life of the process, each one carrying the capability envelope it executed under, so
/// sustained authenticated use grows memory without bound. Mirrors
/// <c>BundleWorkspaceCleanupService</c>, which does the same job for the bundle path.
/// </para>
/// <para>
/// Only terminal runs are ever reclaimed — that rule lives in the store, not here, so no scheduling
/// change can make a run a caller is still polling disappear. The progress broker's bookkeeping is
/// released for the same runs at the same moment, because it is keyed by the same identifier and
/// nothing can publish for a reclaimed run.
/// </para>
/// </remarks>
internal sealed class RunRecordCleanupService : BackgroundService
{
    /// <summary>
    /// Floor on the sweep interval. Configuration is validated as positive, but a value of, say, a
    /// microsecond is positive and would turn this into a spin loop; the floor makes a mis-set value
    /// slow rather than harmful.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Attributed to the host, not a person: nobody asked for a ceiling expiry, so naming a human in
    /// the escalation audit would be untrue. Matches the <c>system:</c> convention
    /// <see cref="IEscalationService.CancelEscalationAsync"/> documents for internal callers.
    /// </summary>
    private const string SystemActor = "system:parked-run-expiry";

    private readonly IRunJobStore _store;
    private readonly IRunProgressBroker _progress;
    private readonly IEscalationService _escalations;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<RunRecordCleanupService> _logger;

    /// <summary>Initializes a new <see cref="RunRecordCleanupService"/>.</summary>
    public RunRecordCleanupService(
        IRunJobStore store,
        IRunProgressBroker progress,
        IEscalationService escalations,
        IOptionsMonitor<AppConfig> config,
        ILogger<RunRecordCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _progress = progress;
        _escalations = escalations;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _config.CurrentValue.AI.WorkflowSubmission.RunSweepInterval;
                if (interval < MinInterval)
                    interval = MinInterval;

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await SweepAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task SweepAsync()
    {
        try
        {
            // Ageing out abandoned gates first, so a run that expires on this tick is reclaimable on
            // this tick rather than waiting a whole interval to become eligible.
            await ExpireStaleParkedRunsAsync().ConfigureAwait(false);

            var reclaimed = _store.SweepExpired();
            if (reclaimed.Count == 0)
                return;

            // A run's identifier keys more than its record. Reclaiming one without the other would
            // trade a bounded leak for an unbounded one — the progress bookkeeping would outlive every
            // run the host ever streamed.
            foreach (var jobId in reclaimed)
                _progress.Forget(jobId);

            _logger.LogInformation(
                "Run sweep reclaimed {Count} expired run record(s).", reclaimed.Count);
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the service down: the next tick would never come, and the
            // retention window would stop being honoured for the life of the process.
            _logger.LogError(ex, "Run record sweep failed; will retry on the next interval.");
        }
    }

    /// <summary>
    /// Gives up on runs that have been waiting for an approval far longer than the host permits, and
    /// withdraws the approvals they were waiting on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Withdrawing is not tidy-up here, it is the same requirement the cancel path has and for a
    /// sharper reason. Failing the run makes it terminal, which <em>unlocks its workflow</em> — so a
    /// second run can start against the same persisted plan, and an approver answering the first run's
    /// gate would have that verdict reconciled into the second. Leaving the approval pending is
    /// therefore not merely confusing for the approver; it is a decision applied to work it was never
    /// about.
    /// </para>
    /// <para>
    /// Logged at warning rather than information: every run this touches is one a human was asked to
    /// decide and never did. That is worth someone's attention, whereas an ordinary reclaim is not.
    /// </para>
    /// <para>
    /// Attributed to a system actor rather than a person, because no person asked for this — the host
    /// gave up on its own schedule, and an audit row naming a human would be untrue.
    /// </para>
    /// </remarks>
    private async Task ExpireStaleParkedRunsAsync()
    {
        var ceiling = _config.CurrentValue.AI.WorkflowSubmission.MaxParkedRunDuration;

        var expired = _store.ExpireStaleParkedRuns(ceiling);
        if (expired.Count == 0)
            return;

        _logger.LogWarning(
            "Failed {Count} run(s) parked longer than {Ceiling} awaiting an approval that never arrived.",
            expired.Count, ceiling);

        foreach (var abandoned in expired)
        {
            await ApprovalWithdrawal.WithdrawAsync(
                _escalations,
                _logger,
                abandoned,
                "The run this approval was raised for waited too long and was given up on.",
                SystemActor,
                CancellationToken.None).ConfigureAwait(false);
        }
    }
}
