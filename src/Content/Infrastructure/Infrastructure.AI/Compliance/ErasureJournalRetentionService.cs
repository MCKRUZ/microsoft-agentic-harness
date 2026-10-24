using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compliance;

/// <summary>
/// Removes erasure journal rows older than <c>AppConfig:AI:Erasure:JournalRetention</c> once an hour (#907): withheld
/// conversation ids, and the ids of a failed request that was never retried. Always on, because the journal holds people's
/// ids and its retention is what bounds it.
/// </summary>
internal sealed class ErasureJournalRetentionService : PeriodicSweepService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly EfCoreErasureJournal _journal;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new instance of the <see cref="ErasureJournalRetentionService"/> class.</summary>
    /// <param name="journal">The journal to sweep.</param>
    /// <param name="config">Supplies the retention, read on every sweep.</param>
    /// <param name="timeProvider">Paces the sweep.</param>
    /// <param name="logger">Logger for what each sweep removed and for failures.</param>
    public ErasureJournalRetentionService(
        EfCoreErasureJournal journal,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ErasureJournalRetentionService> logger)
        : base(Interval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(config);
        _journal = journal;
        _config = config;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention() =>
        new(Enabled: true, Interval, _config.CurrentValue.AI.Erasure.JournalRetention);

    /// <inheritdoc />
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken) =>
        _journal.PurgeOlderThanAsync(gracePeriod, cancellationToken);

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Erasure journal sweep removed {Count} row(s) past the journal retention.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Erasure journal sweep failed; will retry on the next interval.";
}
