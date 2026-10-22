using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Infrastructure.AI.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Deletes plans nothing has happened to for <c>AppConfig:AI:Planner:Retention:RetainFor</c> (#901).
/// </summary>
internal sealed class PlanRetentionService : PeriodicSweepService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlanRetentionService> _logger;

    /// <summary>Initializes a new <see cref="PlanRetentionService"/>.</summary>
    /// <param name="scopes">Creates the scope each sweep resolves the plan store in; the store is registered scoped.</param>
    /// <param name="config">Supplies the period and interval, read live on every tick.</param>
    /// <param name="timeProvider">Drives the wait and the cutoff.</param>
    /// <param name="logger">Receives the per-sweep result and any failure.</param>
    public PlanRetentionService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<PlanRetentionService> logger)
        : base(MinInterval, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(config);

        _scopes = scopes;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override SweepRetentionSnapshot ReadRetention()
    {
        var retention = _config.CurrentValue.AI.Planner.Retention;
        return new SweepRetentionSnapshot(retention.Enabled, retention.SweepInterval, retention.RetainFor);
    }

    /// <inheritdoc />
    /// <remarks>A period under a day is refused here as well as at startup, because a reloaded value is not re-validated.</remarks>
    protected override async Task<int> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, RetentionPeriodConfig.MinimumRetainFor);

        await using var scope = _scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (services.GetRequiredService<IPlanStateStore>() is not EfCorePlanStateStore store)
        {
            _logger.LogWarning("Plan retention is enabled, but the plan store is not the built-in one; no plan will be deleted.");
            return 0;
        }

        var inUse = await PlansInUseAsync(services, cancellationToken);
        return await store.DeleteIdleSinceAsync(_timeProvider.GetUtcNow() - gracePeriod, inUse, cancellationToken);
    }

    /// <summary>
    /// Plans something still refers to: every schedule's workflow target, enabled or not, and every live or parked run's.
    /// Read before the delete, so a schedule created after this read is answered by the next sweep; a plan it names was
    /// already idle for the whole period.
    /// </summary>
    private static async Task<Func<Guid, bool>> PlansInUseAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        if (services.GetService<IScheduleStore>() is { } schedules)
            targets.AddRange(await schedules.ListTargetIdsAsync(RunKind.Workflow, cancellationToken));

        var runs = services.GetService<IRunJobStore>();
        if (runs is not null)
            targets.AddRange(runs.GetParkedRuns().Where(r => r.Kind == RunKind.Workflow).Select(r => r.TargetId));

        var scheduled = targets.Select(t => Guid.TryParse(t, out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet();
        return id => scheduled.Contains(id) || runs?.FindLiveRunForTarget(RunKind.Workflow, id.ToString()) is not null;
    }

    /// <inheritdoc />
    protected override string ReclaimedLogMessage => "Plan retention deleted {Count} plan(s) nothing had happened to for the retention period.";

    /// <inheritdoc />
    protected override string FailureLogMessage => "Plan retention sweep failed; will retry on the next interval.";
}
