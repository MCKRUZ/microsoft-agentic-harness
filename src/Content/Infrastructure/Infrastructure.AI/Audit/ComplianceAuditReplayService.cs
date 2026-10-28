using Application.Core.Compliance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Writes compliance records held pending into the trail (#995): once as the host starts, then every
/// <see cref="Interval"/>, through <see cref="JsonlComplianceAuditTrail.ReplayPendingAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// A record is held when the trail cannot be appended to at the moment an erasure, export or document removal finishes;
/// usually because the chain was busy, so a minute later it can be. A pass that finds nothing held does no more than check
/// that the pending folder exists.
/// </para>
/// <para>
/// Replays only the shipped <see cref="JsonlComplianceAuditTrail"/>. A host that replaces <see cref="IComplianceAuditTrail"/>
/// gets nothing from this service, which logs that once and stops; a replacement owns its own failures.
/// </para>
/// <para>
/// A failing pass is logged and retried at the next interval, never allowed to stop the host.
/// </para>
/// </remarks>
public sealed class ComplianceAuditReplayService : BackgroundService
{
    /// <summary>How long the service waits between passes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IComplianceAuditTrail _trail;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComplianceAuditReplayService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComplianceAuditReplayService"/> class.</summary>
    /// <param name="trail">The host's compliance audit trail.</param>
    /// <param name="timeProvider">Schedules the passes.</param>
    /// <param name="logger">Logger for what each pass did.</param>
    public ComplianceAuditReplayService(
        IComplianceAuditTrail trail, TimeProvider timeProvider, ILogger<ComplianceAuditReplayService> logger)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _trail = trail;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_trail is not JsonlComplianceAuditTrail trail)
        {
            _logger.LogInformation(
                "The compliance audit trail is {TrailType}, not the shipped trail; nothing is replayed.", _trail.GetType().Name);
            return;
        }

        // Off the start-up path: the host does not wait for the first pass.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReplayOnceAsync(trail, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay of held compliance records failed; retrying in {Interval}.", Interval);
            }

            try
            {
                await Task.Delay(Interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReplayOnceAsync(JsonlComplianceAuditTrail trail, CancellationToken cancellationToken)
    {
        var pass = await trail.ReplayPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pass.Written > 0 || pass.AlreadyInTrail > 0)
            _logger.LogInformation(
                "Wrote {Written} held compliance record(s) to {AuditLocation}; {AlreadyInTrail} were already there.",
                pass.Written, trail.AuditLocation, pass.AlreadyInTrail);

        if (pass.Skipped && pass.Remaining > 0)
            _logger.LogWarning(
                "Replay of {Remaining} held compliance record(s) in {PendingLocation} skipped: {Reason}. Once is another " +
                "host's pass; every minute means the folder cannot be locked.",
                pass.Remaining, trail.PendingLocation, pass.SkippedBecause);
        else if (pass.Remaining > 0)
            _logger.LogWarning(
                "{Remaining} compliance record(s) are still held in {PendingLocation}, not yet in the trail.",
                pass.Remaining, trail.PendingLocation);
    }
}
