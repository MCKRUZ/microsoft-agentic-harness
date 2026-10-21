using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Prompts.Persistence;

/// <summary>
/// Deletes persisted prompt-usage rows older than <c>AppConfig:AI:PromptUsage:Retention:RetainFor</c> (#901). Registered
/// only when prompt-usage persistence is on, because only then is there a table to sweep.
/// </summary>
internal sealed class PromptUsageRetentionService : PeriodicSweepService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly EfCorePromptUsageStore _store;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="PromptUsageRetentionService"/>.</summary>
    /// <param name="store">Owns the table and the delete.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public PromptUsageRetentionService(
        EfCorePromptUsageStore store,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<PromptUsageRetentionService> logger)
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
        var retention = _config.CurrentValue.AI.PromptUsage.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        return _store.DeleteRecordedBeforeAsync(_timeProvider.GetUtcNow() - gracePeriod, cancellationToken);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Prompt-usage retention deleted {Count} row(s) past the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Prompt-usage retention sweep failed; will retry on the next interval.";
}
