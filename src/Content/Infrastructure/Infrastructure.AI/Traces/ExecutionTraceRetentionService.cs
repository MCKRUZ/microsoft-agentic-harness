using Application.AI.Common.Interfaces.Traces;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Traces;

/// <summary>
/// Deletes execution-trace runs nothing has written to for <c>AppConfig:MetaHarness:TraceRetention:RetainFor</c> (#901).
/// </summary>
/// <remarks>
/// Acts only when the registered <see cref="IExecutionTraceStore"/> is the shipped <see cref="FileSystemExecutionTraceStore"/>;
/// a replacement store governs its own retention. Runs whether or not production tracing is on, because evaluation runs
/// write traces regardless.
/// </remarks>
internal sealed class ExecutionTraceRetentionService : PeriodicSweepService
{
    /// <summary>The most runs one sweep removes, so a first sweep over a large backlog does not hold the disk for long.</summary>
    internal const int MaxRunsPerSweep = 500;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IExecutionTraceStore _store;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="ExecutionTraceRetentionService"/>.</summary>
    /// <param name="store">The registered trace store; swept only when it is the file-system one.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ExecutionTraceRetentionService(
        IExecutionTraceStore store,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ExecutionTraceRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        _store = store;
        _config = config;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.MetaHarness.TraceRetention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        return _store is FileSystemExecutionTraceStore files
            ? files.DeleteRunsNotWrittenSinceAsync(_timeProvider.GetUtcNow() - gracePeriod, MaxRunsPerSweep, cancellationToken)
            : Task.FromResult(0);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Trace retention deleted {Count} execution-trace run(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Trace retention sweep failed; will retry on the next interval.";
}
