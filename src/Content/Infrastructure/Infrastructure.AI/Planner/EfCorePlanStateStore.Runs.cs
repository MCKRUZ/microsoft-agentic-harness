using System.Text.Json;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Planner;
using Domain.Common;
using Domain.Common.Compliance;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore
{
    /// <summary>
    /// How long past the spill sweep's own period a run's tool-result scope is remembered. The row is refreshed when the
    /// run starts, and its tool steps may spill until it ends.
    /// </summary>
    private static readonly TimeSpan ToolResultScopeMargin = TimeSpan.FromDays(1);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One row per distinct plan, owner, run scope and tool-result scope; running again under the same ones refreshes its
    /// time. A workflow run gets a new tool-result scope every time, so rows for the same plan, owner and run scope that
    /// the spill sweep has outlived are pruned here, keeping the newest so the run scope itself is never forgotten while
    /// the plan exists. When the spill sweep is off, spills are kept indefinitely and so are these rows.
    /// </para>
    /// <para>
    /// Only a plan the caller can see is recorded, so no run can be recorded against another person's plan. A plan deleted
    /// since the executor loaded it (by retention or erasure) is reported as not found rather than thrown.
    /// </para>
    /// </remarks>
    public async Task<Result> RecordRunAsync(PlanId planId, string runScope, string toolResultScopeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolResultScopeId);

        var owner = ScopeIdentity.Canonicalize(_scope.UserId);
        if (owner is null)
            return Result.Success();
        if (owner.Length > PlannerScopeFilter.MaxIdentityLength)
            return Result.Fail("Ambient scope identity exceeds the maximum supported length.");

        var now = _timeProvider.GetUtcNow();
        await using var ctx = _factory.CreateDbContext();
        if (!await IsPlanVisibleAsync(ctx, planId.Value, ct))
            return Result.NotFound($"No plan {planId} found.");

        if (!await TryRefreshRunAsync(ctx, planId.Value, owner, runScope, toolResultScopeId, now, isRunning: true, ct))
        {
            ctx.PlanRuns.Add(new PlanRunEntity
            {
                PlanGraphId = planId.Value,
                OwnerId = owner,
                RunScope = runScope,
                ToolResultScopeId = toolResultScopeId,
                LastRunAt = now,
                IsRunning = true,
            });
            try
            {
                await ctx.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsConstraint(ex, SqliteErrorCodes.ConstraintForeignKey))
            {
                return Result.NotFound($"No plan {planId} found.");
            }
            catch (DbUpdateException ex) when (IsConstraint(ex, SqliteErrorCodes.ConstraintPrimaryKey, SqliteErrorCodes.ConstraintUnique))
            {
                // Another process recorded the same run between the refresh and the insert.
                ctx.ChangeTracker.Clear();
                await TryRefreshRunAsync(ctx, planId.Value, owner, runScope, toolResultScopeId, now, isRunning: true, ct);
            }
        }

        await PruneOutlivedToolScopesAsync(ctx, planId.Value, owner, runScope, now, ct);
        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Updates the row only, never inserts one: a run whose record was erased or whose plan was deleted while it ran is
    /// not recorded again by its own ending.
    /// </remarks>
    public async Task<Result> RecordRunEndedAsync(PlanId planId, string runScope, string toolResultScopeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolResultScopeId);

        var owner = ScopeIdentity.Canonicalize(_scope.UserId);
        if (owner is null)
            return Result.Success();

        await using var ctx = _factory.CreateDbContext();
        await TryRefreshRunAsync(
            ctx, planId.Value, owner, runScope, toolResultScopeId, _timeProvider.GetUtcNow(), isRunning: false, ct);
        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// For every run the person made: each model step of the plan that ran, under the run's scope, and the run's
    /// tool-result scope and budget key. Only model steps run as conversations of their own. Each sub-plan the plan names
    /// is followed, since its steps ran under the same scope. A plan run inside the person's workflow as a shared sub-plan
    /// is also recorded against them, so its steps are found here even though the plan is not theirs.
    /// </para>
    /// <para>
    /// The person's own plans also contribute their steps, and their sub-plans' steps, under the plan id, the run scope
    /// every workflow run uses. That reaches the step sessions of runs made before runs were recorded; their tool-result
    /// scopes were never kept, and the spill sweep removes those within its own period.
    /// </para>
    /// <para>
    /// <strong>A shared plan run under its own id is left out.</strong> A run whose scope is the id of a plan the person
    /// does not own would derive the same step ids for everyone who ran that plan, so erasing one person would reach the
    /// others' records. No signed-in caller can start such a run today; only its own tool-result scope is listed if one
    /// ever is.
    /// </para>
    /// <para>
    /// <see cref="PlanRunTraces.ListedRuns"/> names each record as it was read, so erasure deletes exactly those.
    /// </para>
    /// </remarks>
    public async Task<PlanRunTraces> ListRunTracesAsync(string ownerId, CancellationToken cancellationToken)
    {
        var owner = ScopeIdentity.Canonicalize(ownerId);
        if (owner is null)
            return PlanRunTraces.Empty with { ListedRuns = [] };

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var runs = await ctx.PlanRuns.AsNoTracking()
            .Where(r => r.OwnerId == owner)
            .Select(r => new RunRow(r.Id, r.PlanGraphId, r.RunScope, r.ToolResultScopeId, r.LastRunAt, r.IsRunning))
            .ToListAsync(cancellationToken);
        var ownPlanIds = await ctx.PlanGraphs.AsNoTracking()
            .Where(p => p.OwnerId == owner)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var ownPlans = ownPlanIds.ToHashSet();
        var scopes = runs
            .Where(r => ownPlans.Contains(r.PlanGraphId) || r.RunScope != r.PlanGraphId.ToString())
            .Select(r => (r.PlanGraphId, r.RunScope))
            .Concat(ownPlanIds.Select(id => (PlanGraphId: id, RunScope: id.ToString())))
            .Distinct()
            .ToList();

        var stepConversationIds = await ModelStepConversationIdsAsync(ctx, scopes, cancellationToken);
        var runBudgetKeys = scopes.Select(s => PlanRunKeys.RunBudgetKey(s.RunScope)).Distinct(StringComparer.Ordinal).ToList();
        var toolResultScopeIds = runs.Select(r => r.ToolResultScopeId).Distinct(StringComparer.Ordinal).ToList();

        return new PlanRunTraces(stepConversationIds, toolResultScopeIds, runBudgetKeys)
        {
            ListedRuns = [.. runs.Select(r => r.Token)],
        };
    }

    /// <summary>A run record as read, and the token that names it in that state.</summary>
    private sealed record RunRow(
        long Id, Guid PlanGraphId, string RunScope, string ToolResultScopeId, DateTimeOffset LastRunAt, bool IsRunning)
    {
        /// <summary>Changes whenever the run is recorded again, starts or ends, so a record changed since is not this one.</summary>
        public string Token => $"{Id}:{LastRunAt.UtcTicks}:{(IsRunning ? 1 : 0)}";
    }

    /// <summary>
    /// The conversation id of every model step each (plan, run scope) pair could have run, following the sub-plans each
    /// plan names under the same scope. A plan already reached under a scope is not followed again, so a cycle ends.
    /// </summary>
    private static async Task<List<string>> ModelStepConversationIdsAsync(
        PlannerDbContext ctx, IReadOnlyList<(Guid PlanGraphId, string RunScope)> scopes, CancellationToken ct)
    {
        var seen = scopes.ToHashSet();
        var pending = scopes.ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var planIds = pending.Select(p => p.PlanGraphId).Distinct().ToList();
            var steps = (await ctx.PlanSteps.AsNoTracking()
                    .Where(s => planIds.Contains(s.PlanGraphId)
                        && (s.Type == StepType.LlmCall || s.Type == StepType.SubPlanInvocation))
                    .Select(s => new { s.PlanGraphId, s.Id, s.Type, s.ConfigurationJson })
                    .ToListAsync(ct))
                .ToLookup(s => s.PlanGraphId);

            var next = new List<(Guid PlanGraphId, string RunScope)>();
            foreach (var (planId, scope) in pending)
            {
                foreach (var step in steps[planId])
                {
                    if (step.Type == StepType.LlmCall)
                        ids.Add(PlanRunKeys.StepConversationId(scope, new PlanStepId(step.Id)));
                    else if (ChildPlanOf(step.ConfigurationJson) is { } child && seen.Add((child, scope)))
                        next.Add((child, scope));
                }
            }

            pending = next;
        }

        return [.. ids];
    }

    /// <summary>The plan a sub-plan step runs: the one it names, or the one its inline definition is saved as.</summary>
    private static Guid? ChildPlanOf(string configurationJson) =>
        JsonSerializer.Deserialize<StepConfiguration>(configurationJson, JsonOptions) is SubPlanConfig config
            ? (config.ChildPlanId ?? config.InlinePlanDefinition?.Id)?.Value
            : null;

    private static async Task<bool> TryRefreshRunAsync(
        PlannerDbContext ctx, Guid planId, string owner, string runScope, string toolResultScopeId, DateTimeOffset now,
        bool isRunning, CancellationToken ct)
    {
        var refreshed = await ctx.PlanRuns
            .Where(r => r.PlanGraphId == planId && r.OwnerId == owner && r.RunScope == runScope
                && r.ToolResultScopeId == toolResultScopeId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastRunAt, now).SetProperty(r => r.IsRunning, isRunning), ct);
        return refreshed > 0;
    }

    /// <summary>
    /// Deletes this plan's rows for the same owner and run scope whose tool-result scope the spill sweep has outlived.
    /// The row just written is newer than every one of them, so the run scope stays recorded.
    /// </summary>
    private async Task PruneOutlivedToolScopesAsync(
        PlannerDbContext ctx, Guid planId, string owner, string runScope, DateTimeOffset now, CancellationToken ct)
    {
        var spills = _config.CurrentValue.AI.ContextManagement.ToolResultRetention;
        if (!spills.Enabled)
            return;

        var cutoff = now - spills.GracePeriod - ToolResultScopeMargin;
        await ctx.PlanRuns
            .Where(r => r.PlanGraphId == planId && r.OwnerId == owner && r.RunScope == runScope && r.LastRunAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static bool IsConstraint(DbUpdateException ex, params int[] codes) =>
        ex.InnerException is SqliteException sqlite && codes.Contains(sqlite.SqliteExtendedErrorCode);
}
