using Application.AI.Common.Interfaces.Audit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Writes audit records held pending into their trails (#995, #1001): once as the host starts, then every
/// <see cref="Interval"/>, for every registered trail that holds records (<see cref="IReplaysHeldAuditRecords"/>).
/// </summary>
/// <remarks>
/// <para>
/// A record is held when its trail cannot be appended to at the moment it is made; usually because the chain was busy, so
/// a minute later it can be. A pass that finds nothing held does no more than check that each pending folder exists.
/// </para>
/// <para>
/// The trails are the registered <see cref="IVerifiableAuditChain"/>s, the same list scheduled verification and the
/// compliance report walk, so a trail cannot be verified without also being replayed. A trail a host replaced with one
/// that does not hold records is skipped and named once in the log; a replacement owns its own failures.
/// </para>
/// <para>
/// Trails are replayed one after another; a pass that fails for one is logged and the others still run. Nothing a pass
/// does is allowed to stop the host.
/// </para>
/// </remarks>
public sealed class AuditRecordReplayService : BackgroundService
{
    /// <summary>How long the service waits between passes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<IVerifiableAuditChain> _chains;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRecordReplayService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AuditRecordReplayService"/> class.</summary>
    /// <param name="chains">Every audit trail the host registered.</param>
    /// <param name="timeProvider">Schedules the passes.</param>
    /// <param name="logger">Logger for what each pass did.</param>
    public AuditRecordReplayService(
        IEnumerable<IVerifiableAuditChain> chains, TimeProvider timeProvider, ILogger<AuditRecordReplayService> logger)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _chains = [.. chains];
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trails = ReplayableTrails();
        if (trails.Count == 0)
            return;

        // Off the start-up path: the host does not wait for the first pass.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var trail in trails)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                await ReplaySafelyAsync(trail, stoppingToken).ConfigureAwait(false);
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

    private List<IReplaysHeldAuditRecords> ReplayableTrails()
    {
        var trails = new List<IReplaysHeldAuditRecords>();
        foreach (var chain in _chains)
        {
            if (chain is IReplaysHeldAuditRecords trail)
                trails.Add(trail);
            else
                _logger.LogInformation(
                    "The {AuditName} audit trail is {TrailType}, which holds no records for replay; nothing is replayed for it.",
                    chain.AuditName, chain.GetType().Name);
        }

        return trails;
    }

    private async Task ReplaySafelyAsync(IReplaysHeldAuditRecords trail, CancellationToken cancellationToken)
    {
        try
        {
            Report(trail, await trail.ReplayPendingAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the next start replays what is left.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay of held {AuditName} audit records failed; retrying in {Interval}.", trail.AuditName, Interval);
        }
    }

    private void Report(IReplaysHeldAuditRecords trail, PendingRecordsReplay pass)
    {
        if (pass.Written > 0 || pass.AlreadyInTrail > 0)
            _logger.LogInformation(
                "Wrote {Written} held {AuditName} audit record(s) to the trail; {AlreadyInTrail} were already there.",
                pass.Written, trail.AuditName, pass.AlreadyInTrail);

        if (pass.Skipped && pass.Remaining > 0)
            _logger.LogWarning(
                "Replay of {Remaining} held {AuditName} audit record(s) in {PendingLocation} skipped: {Reason}. A sharing " +
                "violation means another host's pass holds the folder; any other reason means it cannot be locked at all.",
                pass.Remaining, trail.AuditName, trail.PendingLocation, pass.SkippedBecause);
        else if (pass.Remaining > 0)
            _logger.LogWarning(
                "{Remaining} {AuditName} audit record(s) are still held in {PendingLocation}, not yet in the trail.",
                pass.Remaining, trail.AuditName, trail.PendingLocation);
    }
}
