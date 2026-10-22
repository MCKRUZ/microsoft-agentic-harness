using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Deletes delegation session files nothing has written to for
/// <c>AppConfig:AI:Orchestration:Subagent:DelegationRetention:RetainFor</c> (#901).
/// </summary>
internal sealed class DelegationRetentionService : PeriodicSweepService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly JsonlDelegationStore _store;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="DelegationRetentionService"/>.</summary>
    /// <param name="store">Owns the files and the delete.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public DelegationRetentionService(
        JsonlDelegationStore store,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<DelegationRetentionService> logger)
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
        var retention = _config.CurrentValue.AI.Orchestration.Subagent.DelegationRetention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        return _store.DeleteNotWrittenSinceAsync(_timeProvider.GetUtcNow() - gracePeriod, cancellationToken);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Delegation retention deleted {Count} session file(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Delegation retention sweep failed; will retry on the next interval.";
}
