using Application.Common.Logging;
using Domain.Common.Config;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.BackgroundServices;

/// <summary>
/// Deletes log runs under <c>AppConfig:Logging:LogsBasePath</c> nothing has written to for
/// <c>AppConfig:Logging:Retention:RetainFor</c> (#901).
/// </summary>
/// <remarks>
/// <para>
/// A run is a folder whose <see cref="LogRunFolder.MarkerFileName"/> names it, so other content in the logs folder (the
/// file-system tool may write there) is never reached. The run either file provider is writing is skipped; a run another
/// process is writing into the same folder is protected only by its age.
/// </para>
/// <para>
/// The providers are resolved when the sweep runs and are registered only when a logs folder is configured, so a host
/// without file logging sweeps nothing.
/// </para>
/// </remarks>
internal sealed class LogRunRetentionService : PeriodicSweepService
{
    /// <summary>The most runs one batch removes; a sweep runs further batches while each comes back full.</summary>
    internal const int MaxRunsPerBatch = 500;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LogRunRetentionService> _logger;

    /// <summary>Initializes a new <see cref="LogRunRetentionService"/>.</summary>
    /// <param name="services">Resolves the file log providers when the sweep runs.</param>
    /// <param name="config">Supplies the folder, period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public LogRunRetentionService(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<LogRunRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        _services = services;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.Logging.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        var root = _config.CurrentValue.Logging.LogsBasePath;
        if (string.IsNullOrWhiteSpace(root))
            return 0;

        var cutoff = (_timeProvider.GetUtcNow() - gracePeriod).UtcDateTime;
        var fullRoot = Path.GetFullPath(root);
        return await SweepBatches.RunAsync(
            () => Task.FromResult(DeleteBatch(fullRoot, cutoff, cancellationToken)), MaxRunsPerBatch);
    }

    private int DeleteBatch(string root, DateTime cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var run in RetentionDirectories.Subdirectories(root).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted >= MaxRunsPerBatch)
                break;

            try
            {
                if (IsCurrentRun(run)
                    || !LogRunFolder.IsMarkedRun(run, RetentionDirectories.PathComparison)
                    || RetentionDirectories.NewestWriteUtc(run) >= cutoff)
                    continue;

                if (RetentionDirectories.DeleteOwnFiles(run, LogRunFolder.MarkerFileName, LogRunFolder.IsOwnFile, cutoff))
                    deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete expired log run {Run}; will retry on the next sweep.", run);
            }
        }

        return deleted;
    }

    /// <summary>Whether either file provider is writing this run now.</summary>
    private bool IsCurrentRun(string run)
    {
        var name = Path.GetFileName(run);
        return new[]
            {
                _services.GetService<FileLoggerProvider>()?.CurrentRunId,
                _services.GetService<StructuredJsonLoggerProvider>()?.CurrentRunId,
            }
            .Any(current => string.Equals(current, name, RetentionDirectories.PathComparison));
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Log retention deleted {Count} log run(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Log retention sweep failed; will retry on the next interval.";
}
