using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Infrastructure.AI.RAG.GraphRag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Memory;

/// <summary>
/// Deletes cross-session memories not recalled or written for
/// <c>AppConfig:AI:Rag:CrossSessionMemory:Retention:RetainFor</c> (#901).
/// </summary>
/// <remarks>
/// Lives here rather than beside the store so it can use <see cref="PeriodicSweepService"/>. A host whose memory store
/// is not the built-in one (the remote store, or cross-session memory switched off) has nothing for it to sweep.
/// </remarks>
internal sealed class CrossSessionMemoryRetentionService : PeriodicSweepService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="CrossSessionMemoryRetentionService"/>.</summary>
    /// <param name="services">Resolves the memory store, which a host may not register.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public CrossSessionMemoryRetentionService(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<CrossSessionMemoryRetentionService> logger)
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
        var retention = _config.CurrentValue.AI.Rag.CrossSessionMemory.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);
        return _services.GetService<ICrossSessionMemoryStore>() is CrossSessionMemoryStore store
            ? store.DeleteNotAccessedSinceAsync(_timeProvider.GetUtcNow() - gracePeriod, cancellationToken)
            : Task.FromResult(0);
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Memory retention deleted {Count} memory(ies) unused for the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Memory retention sweep failed; will retry on the next interval.";
}
