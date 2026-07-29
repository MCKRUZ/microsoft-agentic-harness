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
/// change can make a run a caller is still polling disappear.
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

    private readonly IRunJobStore _store;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<RunRecordCleanupService> _logger;

    /// <summary>Initializes a new <see cref="RunRecordCleanupService"/>.</summary>
    public RunRecordCleanupService(
        IRunJobStore store,
        IOptionsMonitor<AppConfig> config,
        ILogger<RunRecordCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
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
                Sweep();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private void Sweep()
    {
        try
        {
            var reclaimed = _store.SweepExpired();
            if (reclaimed > 0)
                _logger.LogInformation("Run sweep reclaimed {Count} expired run record(s).", reclaimed);
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the service down: the next tick would never come, and the
            // retention window would stop being honoured for the life of the process.
            _logger.LogError(ex, "Run record sweep failed; will retry on the next interval.");
        }
    }
}
