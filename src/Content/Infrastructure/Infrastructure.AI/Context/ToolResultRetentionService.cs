using Application.AI.Common.Interfaces.Context;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliased for the same reason ConversationBudgetRetentionService aliases its own config type: this
// file's namespace and the config namespace both end in `.Context`/`.ContextManagement`, close enough
// that the unqualified name is worth disambiguating on sight.
using RetentionConfig = Domain.Common.Config.AI.ContextManagement.ToolResultRetentionConfig;

namespace Infrastructure.AI.Context;

/// <summary>
/// Reclaims spilled tool-result files nothing will ever fetch again (#559).
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>FileSystemToolResultStore.StoreIfLargeAsync</c> only ever adds files — nothing
/// removed one, on any path, before this existed. That was a bounded nuisance while every spilled copy
/// was scan-cost-bounded (a few tens of kilobytes); #563 raised the cap to <c>MaxSpillChars</c>
/// (megabytes) and stopped redacting at rest, both of which make an unbounded accumulation of
/// unreclaimed files a real cost, not just an untidy one.
/// </para>
/// <para>
/// The periodic wait/enabled/grace-period skeleton lives in <see cref="PeriodicSweepService"/> (#575) —
/// shared with <see cref="Infrastructure.AI.Conversations.ConversationBudgetRetentionService"/>, which
/// this service was originally modelled on line-for-line before the shared shape was extracted: same
/// five conventions — interval and grace period read live so a reload takes effect at the end of the
/// current wait rather than two intervals later, a minimum-interval floor, an injected
/// <see cref="TimeProvider"/> so the schedule is testable at all, unconditional registration with
/// <c>Enabled</c> read live per tick, and every sweep wrapped so one failure costs a tick rather than
/// the service.
/// </para>
/// </remarks>
internal sealed class ToolResultRetentionService : PeriodicSweepService
{
    /// <summary>
    /// Floor on the sweep interval, matching <c>ConversationBudgetRetentionService.MinInterval</c>'s
    /// reasoning: a positive but tiny value would turn this into a delete loop against the same
    /// filesystem <see cref="IToolResultStore"/> is writing to.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IToolResultStore _resultStore;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="ToolResultRetentionService"/>.</summary>
    /// <param name="resultStore">Owns the files and the sweep that reclaims them.</param>
    /// <param name="config">Supplies the interval and grace period, read live on every tick.</param>
    /// <param name="timeProvider">
    /// Drives the wait between sweeps. Injected rather than taken from <see cref="Task.Delay(TimeSpan)"/>
    /// so the schedule is testable at all — see <c>ConversationBudgetRetentionService</c>'s identical
    /// remark.
    /// </param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ToolResultRetentionService(
        IToolResultStore resultStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ToolResultRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(resultStore);
        ArgumentNullException.ThrowIfNull(config);

        _resultStore = resultStore;
        _config = config;
    }

    private RetentionConfig Retention => _config.CurrentValue.AI.ContextManagement.ToolResultRetention;

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.GracePeriod);
    }

    /// <inheritdoc />
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken) =>
        _resultStore.PruneExpiredAsync(gracePeriod, cancellationToken);

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Tool-result retention sweep reclaimed {Count} expired file(s).";

    /// <inheritdoc />
    protected override string FailureLogMessage =>
        "Tool-result retention sweep failed; will retry on the next interval.";
}
