using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Planner;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// <see cref="IPlannedTaskCanceller"/> over the plan store's run records and the process's cancellation registry (#907).
/// </summary>
/// <remarks>
/// <para>
/// A run is in progress when its record is marked running and was last touched within
/// <see cref="EfCorePlanStateStore.RunInProgressFor"/>, the same rule the plan store's erasure applies; an older mark
/// belongs to a run whose host stopped. Each such run of a plan the person owns is signalled through
/// <see cref="IPlanRunCancellationRegistry"/>, which also stops the sub-plans it started, and the records are polled until
/// the runs have marked themselves ended or the wait runs out.
/// </para>
/// <para>
/// The registry reaches only runs in this process. A run on another host is not signalled; it keeps its plan through
/// erasure, as before, and the receipt says to run erasure again.
/// </para>
/// </remarks>
public sealed class PlannedTaskCanceller : IPlannedTaskCanceller
{
    /// <summary>How long erasure waits for the person's runs to end before going on without them.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    private readonly IDbContextFactory<PlannerDbContext> _factory;
    private readonly IPlanRunCancellationRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlannedTaskCanceller> _logger;
    private readonly TimeSpan _wait;

    /// <summary>Initializes a new instance of the <see cref="PlannedTaskCanceller"/> class.</summary>
    /// <param name="factory">Creates short-lived contexts over the planner database.</param>
    /// <param name="registry">Signals runs in progress in this process.</param>
    /// <param name="timeProvider">Decides which run marks are recent enough to be believed, and times the wait.</param>
    /// <param name="logger">Logger for how many runs were stopped.</param>
    /// <param name="wait">How long to wait for the runs to end; <see cref="DefaultWait"/> when omitted.</param>
    public PlannedTaskCanceller(
        IDbContextFactory<PlannerDbContext> factory,
        IPlanRunCancellationRegistry registry,
        TimeProvider timeProvider,
        ILogger<PlannedTaskCanceller> logger,
        TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
        _wait = wait ?? DefaultWait;
    }

    /// <inheritdoc />
    public async Task<int> StopRunningTasksAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var owner = ScopeIdentity.Canonicalize(ownerId)!;

        var running = await RunningPlansAsync(owner, cancellationToken);
        if (running.Count == 0)
            return 0;

        var signalled = running.Where(plan => _registry.TryCancel(new PlanId(plan))).ToHashSet();
        _logger.LogInformation(
            "Erasure signalled {Signalled} of {Running} planned task(s) in progress to stop.", signalled.Count, running.Count);

        // Waiting only makes sense for runs this process signalled: one on another host, or a mark left by a crash, will
        // not end because of anything done here, and a run started during the wait must not stretch it out.
        var deadline = _timeProvider.GetUtcNow() + _wait;
        while (signalled.Count > 0 && _timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(PollEvery, _timeProvider, cancellationToken);
            running = await RunningPlansAsync(owner, cancellationToken);
            signalled.IntersectWith(running);
        }

        return running.Count;
    }

    /// <summary>
    /// The plans <paramref name="owner"/> owns that have a run in progress. Filtered on the plan's owner, never the run's:
    /// signalling is by plan, so a shared plan the person happens to be running would stop everyone's runs of it.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RunningPlansAsync(string owner, CancellationToken cancellationToken)
    {
        var since = _timeProvider.GetUtcNow() - EfCorePlanStateStore.RunInProgressFor;
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.PlanRuns
            .AsNoTracking()
            .Where(r => r.IsRunning && r.LastRunAt > since && r.PlanGraph!.OwnerId == owner)
            .Select(r => r.PlanGraphId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
