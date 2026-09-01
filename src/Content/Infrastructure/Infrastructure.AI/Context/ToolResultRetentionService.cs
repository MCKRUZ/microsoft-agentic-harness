using Application.AI.Common.Interfaces.Context;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliased for the same reason ConversationBudgetRetentionService aliases its own config type: this
// file's namespace and the config namespace both end in `.Context`/`.ContextManagement`, close enough
// that the unqualified name is worth disambiguating on sight.
using RetentionConfig = Domain.Common.Config.AI.ContextManagement.ToolResultRetentionConfig;

namespace Infrastructure.AI.Context;

/// <summary>
/// Reclaims spilled tool-result files nothing will ever fetch again (#559).
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>FileSystemToolResultStore.StoreIfLargeAsync</c> only ever adds files — nothing
/// removed one, on any path, before this existed. That was a bounded nuisance while every spilled copy
/// was scan-cost-bounded (a few tens of kilobytes); #563 raised the cap to <c>MaxSpillChars</c>
/// (megabytes) and stopped redacting at rest, both of which make an unbounded accumulation of
/// unreclaimed files a real cost, not just an untidy one.
/// </para>
/// <para>
/// Modelled on <see cref="Infrastructure.AI.Conversations.ConversationBudgetRetentionService"/>: same
/// five conventions — interval and grace period read live so a reload takes effect at the end of the
/// current wait rather than two intervals later, a minimum-interval floor, an injected
/// <see cref="TimeProvider"/> so the schedule is testable at all, unconditional registration with
/// <c>Enabled</c> read live per tick, and every sweep wrapped so one failure costs a tick rather than
/// the service.
/// </para>
/// </remarks>
internal sealed class ToolResultRetentionService : BackgroundService
{
    /// <summary>
    /// Floor on the sweep interval, matching <c>ConversationBudgetRetentionService.MinInterval</c>'s
    /// reasoning: a positive but tiny value would turn this into a delete loop against the same
    /// filesystem <see cref="IToolResultStore"/> is writing to.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IToolResultStore _resultStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ToolResultRetentionService> _logger;

    /// <summary>Initializes a new <see cref="ToolResultRetentionService"/>.</summary>
    /// <param name="resultStore">Owns the files and the sweep that reclaims them.</param>
    /// <param name="config">Supplies the interval and grace period, read live on every tick.</param>
    /// <param name="timeProvider">
    /// Drives the wait between sweeps. Injected rather than taken from <see cref="Task.Delay(TimeSpan)"/>
    /// so the schedule is testable at all — see <c>ConversationBudgetRetentionService</c>'s identical
    /// remark.
    /// </param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ToolResultRetentionService(
        IToolResultStore resultStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ToolResultRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(resultStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _resultStore = resultStore;
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

                // Read AFTER the wait, not before it — see ConversationBudgetRetentionService's
                // identical comment for why a value captured before the delay would take an extra
                // interval to react to a configuration reload.
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

    private RetentionConfig Retention => _config.CurrentValue.AI.ContextManagement.ToolResultRetention;

    private async Task SweepAsync(TimeSpan gracePeriod)
    {
        try
        {
            var reclaimed = await _resultStore.PruneExpiredAsync(gracePeriod, CancellationToken.None)
                .ConfigureAwait(false);

            if (reclaimed > 0)
            {
                _logger.LogInformation(
                    "Tool-result retention sweep reclaimed {Count} expired file(s).", reclaimed);
            }
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the service down: the next tick would never come, and
            // spilled files would resume accumulating without bound for the life of the process.
            _logger.LogError(ex, "Tool-result retention sweep failed; will retry on the next interval.");
        }
    }
}
