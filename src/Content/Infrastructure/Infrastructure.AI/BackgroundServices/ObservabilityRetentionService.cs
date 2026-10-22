using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.BackgroundServices;

/// <summary>
/// Deletes observability sessions with no activity for <c>AppConfig:Observability:Retention:RetainFor</c>, with the
/// rows recorded under them, plus context snapshots and session-less audit rows older than that (#901).
/// </summary>
/// <remarks>
/// The store is resolved when the sweep runs rather than injected, so a host without observability persistence
/// registers this harmlessly; the no-op store deletes nothing.
/// </remarks>
internal sealed class ObservabilityRetentionService : PeriodicSweepService
{
    /// <summary>The most sessions one sweep removes, so a first sweep over a large backlog does not hold the database for long.</summary>
    internal const int MaxSessionsPerSweep = 1000;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="ObservabilityRetentionService"/>.</summary>
    /// <param name="services">Resolves the observability store when the sweep runs.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ObservabilityRetentionService(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ObservabilityRetentionService> logger)
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
        var retention = _config.CurrentValue.Observability.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        return _services.GetService<IObservabilityStore>() is { } store
            ? store.DeleteInactiveSinceAsync(_timeProvider.GetUtcNow() - gracePeriod, MaxSessionsPerSweep, cancellationToken)
            : Task.FromResult(0);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Observability retention deleted {Count} inactive session(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Observability retention sweep failed; will retry on the next interval.";
}
