using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Infrastructure.AI.StateManagement.Checkpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.StateManagement;

/// <summary>
/// Deletes the saved state of workflows nothing has written for
/// <c>AppConfig:Infrastructure:StateManagement:Retention:RetainFor</c> (#901).
/// </summary>
/// <remarks>
/// The JSON checkpoint store is resolved when the sweep runs; it is registered as the one the
/// <see cref="CompositeStateManager"/> writes through, so a host whose composite wraps another store sweeps nothing.
/// </remarks>
internal sealed class WorkflowStateRetentionService : PeriodicSweepService
{
    /// <summary>The most workflows one batch removes; a sweep runs further batches while each comes back full.</summary>
    internal const int MaxWorkflowsPerBatch = 500;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="WorkflowStateRetentionService"/>.</summary>
    /// <param name="services">Resolves the checkpoint store when the sweep runs.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public WorkflowStateRetentionService(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<WorkflowStateRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        _services = services;
        _config = config;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.Infrastructure.StateManagement.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        if (_services.GetService<JsonCheckpointStateManager>() is not { } checkpoints)
            return 0;

        var cutoff = _timeProvider.GetUtcNow() - gracePeriod;
        return await SweepBatches.RunAsync(
            () => checkpoints.DeleteWorkflowsNotWrittenSinceAsync(cutoff, MaxWorkflowsPerBatch, cancellationToken),
            MaxWorkflowsPerBatch);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Workflow state retention deleted {Count} workflow(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Workflow state retention sweep failed; will retry on the next interval.";
}
