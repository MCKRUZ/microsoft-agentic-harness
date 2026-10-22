using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore
{
    /// <summary>The most plans one retention call deletes.</summary>
    public const int RetentionBatchSize = 500;

    /// <summary>
    /// Deletes every plan nothing has happened to since <paramref name="cutoff"/> (#901): not saved, no step started or
    /// finished, and no execution-log entry. Every owner, and plans with no owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timestamps are compared in memory: they are stored as SQLite text, which does not compare as an instant across
    /// offsets. Candidates are the plans not saved since the cutoff; each is then dropped if any of its steps or log entries
    /// is newer.
    /// </para>
    /// <para>
    /// Children are deleted in dependency order in one transaction, as owner erasure does, and a deleted plan's sub-plans
    /// keep their rows with the link cleared by the schema's <c>SET NULL</c>; each sub-plan ages on its own activity.
    /// </para>
    /// </remarks>
    /// <param name="cutoff">Plans with no activity at or after this instant are deleted.</param>
    /// <param name="isInUse">
    /// True for a plan something still refers to: a schedule that runs it, or a run queued or parked on it. Such a plan
    /// is kept however long it has been idle; a quarterly schedule waits longer than the period between its runs.
    /// </param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>How many plans were deleted, at most <see cref="RetentionBatchSize"/> per call.</returns>
    public async Task<int> DeleteIdleSinceAsync(DateTimeOffset cutoff, Func<Guid, bool> isInUse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isInUse);
        await using var context = await _factory.CreateDbContextAsync(ct);

        // The idle check runs inside the write transaction (Microsoft.Data.Sqlite begins it IMMEDIATE), so no step can
        // start on a plan between the check and the delete. Bounded per call so the write lock is held briefly; the rest
        // go on later sweeps.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var planIds = (await IdlePlanIdsAsync(context, cutoff, ct))
            .Where(id => !isInUse(id))
            .Take(RetentionBatchSize)
            .ToList();
        if (planIds.Count == 0)
            return 0;

        await context.PlanEdges.Where(e => planIds.Contains(e.PlanGraphId)).ExecuteDeleteAsync(ct);
        await context.StepExecutionStates
            .Where(s => context.PlanSteps.Any(step => step.Id == s.StepId && planIds.Contains(step.PlanGraphId)))
            .ExecuteDeleteAsync(ct);
        await context.PlanExecutionLogs.Where(l => planIds.Contains(l.PlanGraphId)).ExecuteDeleteAsync(ct);
        await context.PlanSteps.Where(s => planIds.Contains(s.PlanGraphId)).ExecuteDeleteAsync(ct);
        var removed = await context.PlanGraphs.Where(p => planIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);

        return removed;
    }

    private static async Task<List<Guid>> IdlePlanIdsAsync(
        Persistence.PlannerDbContext context, DateTimeOffset cutoff, CancellationToken ct)
    {
        var plans = await context.PlanGraphs.AsNoTracking()
            .Select(p => new { p.Id, p.UpdatedAt })
            .ToListAsync(ct);
        var candidates = plans.Where(p => p.UpdatedAt < cutoff).Select(p => p.Id).ToHashSet();
        if (candidates.Count == 0)
            return [];

        var stepTimes = await context.StepExecutionStates.AsNoTracking()
            .Join(context.PlanSteps, s => s.StepId, step => step.Id, (s, step) => new { step.PlanGraphId, s.StartedAt, s.CompletedAt })
            .Where(x => candidates.Contains(x.PlanGraphId))
            .ToListAsync(ct);
        var logTimes = await context.PlanExecutionLogs.AsNoTracking()
            .Where(l => candidates.Contains(l.PlanGraphId))
            .Select(l => new { l.PlanGraphId, l.Timestamp })
            .ToListAsync(ct);

        candidates.ExceptWith(stepTimes
            .Where(x => x.StartedAt >= cutoff || x.CompletedAt >= cutoff)
            .Select(x => x.PlanGraphId));
        candidates.ExceptWith(logTimes.Where(x => x.Timestamp >= cutoff).Select(x => x.PlanGraphId));
        return [.. candidates];
    }
}
