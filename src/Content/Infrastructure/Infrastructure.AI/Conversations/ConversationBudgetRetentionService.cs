using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliased because this file's own namespace and the config namespace both end in `.Conversations`, so
// the unqualified name binds to neither without help.
using RetentionConfig = Domain.Common.Config.AI.Conversations.ConversationBudgetRetentionConfig;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Reclaims conversation-budget rows whose conversation no longer exists.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the durable budget table only ever grows. Rows are removed on request by
/// <see cref="SqliteConversationBudgetTracker.ReleaseAsync"/>, but the interactive callers never call it
/// — a turn ending is not a conversation ending — so deleting a conversation left its running total
/// behind permanently. That was tolerable while the conversation ceiling shipped switched off and only
/// deployments that opted in wrote rows at all; it stopped being conditional when the ceiling became a
/// default (issue #253).
/// </para>
/// <para>
/// <strong>What is swept, and what is deliberately not.</strong> Only rows whose conversation is gone.
/// A conversation that merely sits idle keeps its row for as long as it exists, however long that is —
/// see <see cref="SqliteConversationBudgetTracker.SweepAbandonedAsync"/> for why an age-only rule would
/// silently reset the ceiling the budget exists to enforce.
/// </para>
/// <para>
/// Registered only on the SQLite branch. The file-backed provider uses the in-process tracker, which
/// bounds itself by evicting least-recently-used entries and has nothing to sweep.
/// </para>
/// <para>
/// The periodic wait/enabled/grace-period skeleton lives in <see cref="PeriodicSweepService"/> (#575) —
/// shared with <see cref="Infrastructure.AI.Context.ToolResultRetentionService"/>, which this service
/// was originally modelled on line-for-line before the shared shape was extracted.
/// </para>
/// </remarks>
internal sealed class ConversationBudgetRetentionService : PeriodicSweepService
{
    /// <summary>
    /// Floor on the sweep interval. A positive but tiny value would turn this into a delete loop against
    /// the same database the turn lease serialises on; the floor makes a mis-set value slow rather than
    /// harmful, matching <c>RunRecordCleanupService</c>.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly SqliteConversationBudgetTracker _tracker;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="ConversationBudgetRetentionService"/>.</summary>
    /// <param name="tracker">Owns the table and the statement that sweeps it.</param>
    /// <param name="config">Supplies the interval and grace period, read live on every tick.</param>
    /// <param name="timeProvider">
    /// Drives the wait between sweeps. Injected rather than taken from <see cref="Task.Delay(TimeSpan)"/>
    /// so the schedule is testable at all: with a six-hour default and a one-minute floor, a test on the
    /// real clock could only assert that nothing has happened yet.
    /// </param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ConversationBudgetRetentionService(
        SqliteConversationBudgetTracker tracker,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ConversationBudgetRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(config);

        _tracker = tracker;
        _config = config;
    }

    private RetentionConfig Retention => _config.CurrentValue.AI.Conversations.BudgetRetention;

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.GracePeriod);
    }

    /// <inheritdoc />
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken) =>
        // No guard on the grace period here. The tracker refuses a negative one — a cutoff in the
        // future would sweep rows still in use — and a second copy of that rule in this file is a
        // second thing to keep in step. A misconfigured value therefore surfaces through the base
        // class's own failure handling, named, once per interval.
        _tracker.SweepAbandonedAsync(gracePeriod, cancellationToken);

    /// <inheritdoc />
    protected override string ReclaimedLogMessage =>
        "Conversation budget sweep reclaimed {Count} row(s) whose conversation no longer exists.";

    /// <inheritdoc />
    protected override string FailureLogMessage =>
        "Conversation budget sweep failed; will retry on the next interval.";
}
