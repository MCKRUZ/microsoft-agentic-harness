using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Infrastructure.AI.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore : IPersonalDataEraser, IPersonalDataScrubber
{
    /// <inheritdoc/>
    public string ErasureStoreName => "plans";

    /// <inheritdoc/>
    /// <remarks>
    /// In their own stage, after every store their runs lead to (#906): the run records are what lead to the step
    /// sessions, spills and budgets of the person's planned tasks, so they are deleted only once every store reached
    /// through them succeeded, and a failed request can be retried and still find them.
    /// </remarks>
    public ErasureStage ErasureStage => ErasureStage.PlannedTasks;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Matches <see cref="ErasureSubject.CanonicalOwnerId"/>: plans are stamped through
    /// <c>ScopeIdentity.Canonicalize</c>. Returns the number of plans and run records removed.
    /// </para>
    /// <para>
    /// <strong>Children are deleted explicitly, in dependency order, in one transaction.</strong> A plan's edges point at
    /// its steps with a restrictive foreign key, and the database does not promise which of a plan's cascades runs first,
    /// so a plain plan delete can fail on the edge-to-step constraint. Deleting edges, step states, logs and steps before
    /// the plans themselves removes that ordering question. A sub-plan owned by someone else keeps its row; its link to
    /// an erased parent is cleared by the schema's <c>SET NULL</c>.
    /// </para>
    /// <para>
    /// The person's run records go too, including those on plans they did not own (a shared sub-plan run inside their
    /// workflow). Only the owner runs a plan they own, so its run records are all theirs; the schema's cascade would
    /// remove them with the plan regardless.
    /// </para>
    /// <para>
    /// <strong>Only what was listed, and finished.</strong> The stores reached through the runs were erased for the run
    /// records listed in <see cref="PlanRunTraces.ListedRuns"/>. A record not among them, or changed since (run again,
    /// started or ended), or a run still in progress, may have written after those stores were erased: it is kept, with
    /// the plan it ran, and the coordinator tells the person to run erasure again, as it does for a conversation started
    /// meanwhile. A mark of a run in progress older than <see cref="RunInProgressFor"/> is a run that crashed. A subject not
    /// read from a plan store (no listing) removes every run.
    /// </para>
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        // Read inside the write transaction (Microsoft.Data.Sqlite begins it IMMEDIATE), so no run is recorded between
        // deciding what was listed and deleting it.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var owner = subject.CanonicalOwnerId;
        var runs = (await context.PlanRuns.Where(r => r.OwnerId == owner)
                .Select(r => new RunRow(r.Id, r.PlanGraphId, r.RunScope, r.ToolResultScopeId, r.LastRunAt, r.IsRunning))
                .ToListAsync(cancellationToken))
            .Select(r => (Row: r, Settled: IsSettled(r, subject.PlanRuns.ListedRuns)))
            .ToList();
        var keptPlanIds = runs.Where(r => !r.Settled).Select(r => r.Row.PlanGraphId).ToHashSet();
        var removableRunIds = runs.Where(r => r.Settled).Select(r => r.Row.Id).ToList();

        var planIds = (await context.PlanGraphs
                .Where(p => p.OwnerId == owner)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken))
            .Where(id => !keptPlanIds.Contains(id))
            .ToList();
        var runsRemoved = await context.PlanRuns.Where(r => removableRunIds.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        if (planIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return runsRemoved;
        }

        await context.PlanEdges.Where(e => planIds.Contains(e.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        await context.StepExecutionStates
            .Where(s => context.PlanSteps.Any(step => step.Id == s.StepId && planIds.Contains(step.PlanGraphId)))
            .ExecuteDeleteAsync(cancellationToken);
        await context.PlanExecutionLogs.Where(l => planIds.Contains(l.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        await context.PlanSteps.Where(s => planIds.Contains(s.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        var removed = await context.PlanGraphs.Where(p => planIds.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return removed + runsRemoved;
    }

    /// <summary>
    /// How long a run's in-progress mark is believed. Runs are bounded by their plan timeout (an hour at most by default),
    /// so a mark this old belongs to a run whose host stopped before it could end it.
    /// </summary>
    internal static readonly TimeSpan RunInProgressFor = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether erasure may delete this run record: it was listed exactly as it is now and its run is not in progress. With
    /// no listing (a subject not read from a plan store) every record is.
    /// </summary>
    private bool IsSettled(RunRow row, IReadOnlyList<string>? listedRuns)
    {
        if (listedRuns is null)
            return true;
        var inProgress = row.IsRunning && row.LastRunAt > _timeProvider.GetUtcNow() - RunInProgressFor;
        return !inProgress && listedRuns.Contains(row.Token, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public async Task ScrubAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        await context.Database.TruncateWriteAheadLogAsync(cancellationToken);
    }
}
