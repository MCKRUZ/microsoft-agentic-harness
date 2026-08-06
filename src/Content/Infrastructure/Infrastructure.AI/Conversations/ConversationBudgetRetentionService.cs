using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliased because this file's own namespace and the config namespace both end in `.Conversations`, so
// the unqualified name binds to neither without help.
using RetentionConfig = Domain.Common.Config.AI.Conversations.ConversationBudgetRetentionConfig;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Reclaims conversation-budget rows whose conversation no longer exists.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the durable budget table only ever grows. Rows are removed on request by
/// <see cref="SqliteConversationBudgetTracker.ReleaseAsync"/>, but the interactive callers never call it
/// — a turn ending is not a conversation ending — so deleting a conversation left its running total
/// behind permanently. That was tolerable while the conversation ceiling shipped switched off and only
/// deployments that opted in wrote rows at all; it stopped being conditional when the ceiling became a
/// default (issue #253).
/// </para>
/// <para>
/// <strong>What is swept, and what is deliberately not.</strong> Only rows whose conversation is gone.
/// A conversation that merely sits idle keeps its row for as long as it exists, however long that is —
/// see <see cref="SqliteConversationBudgetTracker.SweepAbandonedAsync"/> for why an age-only rule would
/// silently reset the ceiling the budget exists to enforce.
/// </para>
/// <para>
/// Registered only on the SQLite branch. The file-backed provider uses the in-process tracker, which
/// bounds itself by evicting least-recently-used entries and has nothing to sweep.
/// </para>
/// <para>
/// Modelled on <c>RunRecordCleanupService</c>: interval read live so a configuration reload takes
/// effect, floored so a mis-set value is slow rather than harmful, and every sweep wrapped so a failure
/// costs one tick rather than the service.
/// </para>
/// </remarks>
internal sealed class ConversationBudgetRetentionService : BackgroundService
{
    /// <summary>
    /// Floor on the sweep interval. A positive but tiny value would turn this into a delete loop against
    /// the same database the turn lease serialises on; the floor makes a mis-set value slow rather than
    /// harmful, matching <c>RunRecordCleanupService</c>.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly SqliteConversationBudgetTracker _tracker;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationBudgetRetentionService> _logger;

    /// <summary>Initializes a new <see cref="ConversationBudgetRetentionService"/>.</summary>
    /// <param name="tracker">Owns the table and the statement that sweeps it.</param>
    /// <param name="config">Supplies the interval and grace period, read live on every tick.</param>
    /// <param name="timeProvider">
    /// Drives the wait between sweeps. Injected rather than taken from <see cref="Task.Delay(TimeSpan)"/>
    /// so the schedule is testable at all: with a six-hour default and a one-minute floor, a test on the
    /// real clock could only assert that nothing has happened yet.
    /// </param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ConversationBudgetRetentionService(
        SqliteConversationBudgetTracker tracker,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ConversationBudgetRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _tracker = tracker;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = Retention.SweepInterval;
                if (interval < MinInterval)
                    interval = MinInterval;

                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);

                // Read AFTER the wait, not before it. IOptionsMonitor hands back a fresh AppConfig on
                // reload, so a value captured before a six-hour delay is a value from six hours ago, and
                // a configuration change would take two intervals to be acted on instead of one.
                var retention = Retention;

                if (!retention.Enabled)
                    continue;

                await SweepAsync(retention.GracePeriod).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private RetentionConfig Retention => _config.CurrentValue.AI.Conversations.BudgetRetention;

    private async Task SweepAsync(TimeSpan gracePeriod)
    {
        try
        {
            // No guard on the grace period here. The tracker refuses a negative one — a cutoff in the
            // future would sweep rows still in use — and a second copy of that rule in this file is a
            // second thing to keep in step. A misconfigured value therefore surfaces through the catch
            // below, named, once per interval.
            var reclaimed = await _tracker.SweepAbandonedAsync(gracePeriod, CancellationToken.None)
                .ConfigureAwait(false);

            if (reclaimed > 0)
            {
                _logger.LogInformation(
                    "Conversation budget sweep reclaimed {Count} row(s) whose conversation no longer exists.",
                    reclaimed);
            }
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the service down: the next tick would never come, and the
            // table would resume growing without bound for the life of the process.
            _logger.LogError(ex, "Conversation budget sweep failed; will retry on the next interval.");
        }
    }
}
