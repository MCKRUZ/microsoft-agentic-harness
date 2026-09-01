using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.BackgroundServices;

/// <summary>
/// A configuration snapshot for one <see cref="PeriodicSweepService"/> tick, read atomically so
/// <see cref="Enabled"/> and <see cref="GracePeriod"/> always come from the same underlying config
/// generation rather than two independently-timed reads that a reload could straddle.
/// </summary>
/// <param name="Enabled">Whether the sweep should run this tick.</param>
/// <param name="SweepInterval">How long to wait before the next tick.</param>
/// <param name="GracePeriod">How stale something must be before this tick's sweep reclaims it.</param>
internal readonly record struct SweepRetentionSnapshot(bool Enabled, TimeSpan SweepInterval, TimeSpan GracePeriod);

/// <summary>
/// Shared periodic-sweep skeleton (#575): reads a live retention config, waits, and — when enabled —
/// runs one sweep, forever, until the host stops.
/// </summary>
/// <remarks>
/// <para>
/// Extracted after <c>ConversationBudgetRetentionService</c> and <c>ToolResultRetentionService</c> were
/// found to have token-for-token identical <c>ExecuteAsync</c> bodies apart from the collaborator
/// invoked and the log text — the second was explicitly modelled on the first, and both drifted apart
/// only in ways this base class now expresses as the two things a subclass actually has to supply: what
/// to sweep, and what to say about it.
/// </para>
/// <para>
/// <strong>Deliberately not applied to <c>RunRecordCleanupService</c>.</strong> That service has no
/// <see cref="TimeProvider"/> (a real <c>Task.Delay</c>, untestable on schedule), no <c>Enabled</c>
/// gate (it always sweeps), no grace period (its sweep is nullary — the TTL rule lives in its own
/// store), a different return shape (<c>IReadOnlyList&lt;string&gt;</c>, not a count), and performs
/// multiple distinct sub-operations per cycle with per-listener fan-out. Forcing it into this shape
/// would cost more than the ~25 lines of duplication it would save — an explicit skip, not an
/// oversight.
/// </para>
/// <para>
/// <strong>Interval is read BEFORE the wait; <see cref="SweepRetentionSnapshot.Enabled"/> and
/// <see cref="SweepRetentionSnapshot.GracePeriod"/> are read AFTER it, from a separate, later
/// snapshot.</strong> This is deliberate, not an inconsistency: the interval for THIS tick's wait must
/// be decided before the wait starts, but a configuration change during a potentially long wait (six
/// hours, in the conversation-budget case) must still be honoured for whether and how the sweep that
/// follows actually runs — reading a value captured before the wait for those two would delay a
/// reaction to a reload by a full extra interval.
/// </para>
/// </remarks>
internal abstract class PeriodicSweepService : BackgroundService
{
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="minInterval">
    /// Floor on the sweep interval. A positive but tiny configured value would turn this into a delete
    /// loop against whatever store the sweep targets; the floor makes a mis-set value slow rather than
    /// harmful.
    /// </param>
    /// <param name="timeProvider">
    /// Drives the wait between sweeps. Injected rather than a bare <see cref="Task.Delay(TimeSpan)"/> so
    /// the schedule is testable at all — a real multi-hour default interval on the real clock could only
    /// ever assert that nothing has happened yet.
    /// </param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    protected PeriodicSweepService(TimeSpan minInterval, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _minInterval = minInterval;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Reads the live retention configuration. Called once per use, never cached.</summary>
    protected abstract SweepRetentionSnapshot ReadRetention();

    /// <summary>Performs one sweep and returns the count of items reclaimed.</summary>
    protected abstract Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);

    /// <summary>
    /// Structured-logging message template for a sweep that reclaimed at least one item. Must contain a
    /// <c>{Count}</c> placeholder — passed straight to <c>ILogger.LogInformation</c>, not pre-formatted,
    /// so the count stays a structured field rather than baked into message text.
    /// </summary>
    protected abstract string ReclaimedLogMessage { get; }

    /// <summary>Log message for a sweep that threw.</summary>
    protected abstract string FailureLogMessage { get; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = ReadRetention().SweepInterval;
                if (interval < _minInterval)
                    interval = _minInterval;

                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);

                var retention = ReadRetention();
                if (!retention.Enabled)
                    continue;

                await SweepOnceAsync(retention.GracePeriod).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task SweepOnceAsync(TimeSpan gracePeriod)
    {
        try
        {
            // CancellationToken.None, deliberately: a sweep already in flight when the host starts
            // stopping should still finish this one tick rather than being cut off mid-reclaim — the
            // OUTER loop's own stoppingToken-driven cancellation (via Task.Delay above) is what actually
            // stops the service from scheduling a NEXT tick.
            var reclaimed = await SweepAsync(gracePeriod, CancellationToken.None).ConfigureAwait(false);

            if (reclaimed > 0)
                _logger.LogInformation(ReclaimedLogMessage, reclaimed);
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the service down: the next tick would never come, and
            // whatever this sweep reclaims would resume accumulating without bound for the life of the
            // process.
            _logger.LogError(ex, FailureLogMessage);
        }
    }
}
