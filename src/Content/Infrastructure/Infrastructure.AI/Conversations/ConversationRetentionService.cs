using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliased for the same reason ConversationBudgetRetentionService aliases its config: this namespace and the config's both
// end in `.Conversations`.
using RetentionConfig = Domain.Common.Config.AI.Conversations.ConversationRetentionConfig;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Runs <see cref="IConversationExpiry"/> on a schedule, so conversations nobody has used for
/// <see cref="RetentionConfig.IdlePeriod"/> are deleted with everything that belongs to them (#901).
/// </summary>
/// <remarks>
/// Registered for both conversation providers and unconditionally: <see cref="RetentionConfig.Enabled"/> is read live on
/// every tick, so a host can turn expiry on or off without restarting. Each sweep resolves the expiry in its own scope,
/// because the stores it erases through are registered scoped.
/// </remarks>
internal sealed class ConversationRetentionService : PeriodicSweepService
{
    /// <summary>Floor on the sweep interval, matching the other sweeps on the conversations database.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new <see cref="ConversationRetentionService"/>.</summary>
    /// <param name="scopes">Creates the scope each sweep resolves the expiry and its stores in.</param>
    /// <param name="config">Supplies the period, interval and batch size, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait between sweeps.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public ConversationRetentionService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<ConversationRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(config);

        _scopes = scopes;
        _config = config;
    }

    private RetentionConfig Retention => _config.CurrentValue.AI.Conversations.Retention;

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.IdlePeriod);
    }

    /// <inheritdoc />
    /// <remarks>The expiry refuses a period under a day itself, so a mistyped reload fails the sweep instead of running it.</remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var expiry = scope.ServiceProvider.GetRequiredService<IConversationExpiry>();
        return await expiry.ExpireIdleAsync(gracePeriod, Retention.BatchSize, cancellationToken);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage =>
        "Conversation retention deleted {Count} conversation(s) nobody had used for the configured idle period.";

    /// <inheritdoc />
    protected override string FailureLogMessage =>
        "Conversation retention sweep failed; will retry on the next interval.";
}
