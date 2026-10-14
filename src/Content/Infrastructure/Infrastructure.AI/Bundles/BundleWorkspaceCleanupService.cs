using Application.AI.Common.Interfaces.Bundles;
using Application.Common.Helpers;
using Domain.Common.Config;
using Domain.Common.Config.AI.BundleExecution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// Periodically sweeps expired staged-bundle handles (deleting their staging directories) and expired run
/// records. This is the belt-and-suspenders backstop behind the handle store's own explicit-delete and
/// lease-release cleanup paths — it guarantees an abandoned staging directory is removed even when no caller
/// ever deletes the handle, and reclaims completed run records after their pollable window. Mirrors
/// <c>SessionIdleCleanupService</c>.
/// </summary>
internal sealed class BundleWorkspaceCleanupService : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IBundleHandleStore _handleStore;
    private readonly IBundleRunJobStore _jobStore;
    private readonly LastKnownGoodSetting<TimeSpan> _cleanupInterval;
    private readonly ILogger<BundleWorkspaceCleanupService> _logger;

    /// <summary>Initializes a new <see cref="BundleWorkspaceCleanupService"/>.</summary>
    public BundleWorkspaceCleanupService(
        IBundleHandleStore handleStore,
        IBundleRunJobStore jobStore,
        IOptionsMonitor<AppConfig> config,
        ILogger<BundleWorkspaceCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _handleStore = handleStore;
        _jobStore = jobStore;
        _logger = logger;

        // A hot-reloaded value that fails validation makes CurrentValue throw; read it through a
        // last-known-good guard so that cannot escape ExecuteAsync and stop the host (#821).
        _cleanupInterval = new LastKnownGoodSetting<TimeSpan>(
            () => config.CurrentValue.AI.BundleExecution.CleanupInterval,
            new BundleExecutionConfig().CleanupInterval,
            logger,
            "AppConfig:AI:BundleExecution:CleanupInterval");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _cleanupInterval.Current;
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
            var handles = _handleStore.SweepExpired();
            var jobs = _jobStore.SweepExpired();

            if (handles > 0 || jobs > 0)
            {
                _logger.LogInformation(
                    "Bundle workspace sweep evicted {HandleCount} expired handle(s) and {JobCount} expired run record(s).",
                    handles, jobs);
            }
        }
        catch (Exception ex)
        {
            // A sweep failure must not tear down the background service; log and try again next tick.
            _logger.LogError(ex, "Bundle workspace cleanup sweep failed; will retry on the next interval.");
        }
    }
}
