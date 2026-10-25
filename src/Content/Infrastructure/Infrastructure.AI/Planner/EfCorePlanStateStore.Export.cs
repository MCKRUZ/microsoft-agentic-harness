using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

/// <summary>Personal-data export (#902): the person's plans, everything recorded under them, and their runs.</summary>
public sealed partial class EfCorePlanStateStore : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <summary>
    /// <para>
    /// Every plan the person owns, with its steps, step states (outputs, errors, attestations), edges, execution log and
    /// run records, and every run record they made on a plan they do not own: the rows erasure deletes, matched the same
    /// way, on <see cref="ExportSubject.CanonicalOwnerId"/>.
    /// </para>
    /// <para>
    /// A run on someone else's plan (a shared sub-plan inside the person's workflow) is returned as the run record alone;
    /// the plan it ran belongs to its owner and is not part of this copy.
    /// </para>
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One record per owned plan, then one per run on a plan the person does not own.</returns>
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var owner = subject.CanonicalOwnerId;
        var runs = await RunsOwnedBy(context, owner).AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
        // Ordered here: SQLite cannot order by a DateTimeOffset column.
        var plans = (await PlansOwnedBy(context, owner).AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(p => p.CreatedAt)
            .ToList();

        var records = new List<JsonElement>();
        foreach (var plan in plans)
            records.Add(JsonSerializer.SerializeToElement(await ReadPlanAsync(context, plan, runs, cancellationToken), ExportJson));

        var ownedPlanIds = plans.Select(p => p.Id).ToHashSet();
        records.AddRange(runs
            .Where(r => !ownedPlanIds.Contains(r.PlanGraphId))
            .Select(r => JsonSerializer.SerializeToElement(new ExportedRunOnAnotherPlan(ToRun(r)), ExportJson)));
        return records;
    }

    /// <summary>The plans the person owns. Plans are stamped with the canonical owner. Shared with erasure.</summary>
    private static IQueryable<PlanGraphEntity> PlansOwnedBy(PlannerDbContext context, string canonicalOwner) =>
        context.PlanGraphs.Where(p => p.OwnerId == canonicalOwner);

    /// <summary>The run records the person made, on any plan. Shared with erasure.</summary>
    private static IQueryable<PlanRunEntity> RunsOwnedBy(PlannerDbContext context, string canonicalOwner) =>
        context.PlanRuns.Where(r => r.OwnerId == canonicalOwner);

    private static async Task<ExportedPlan> ReadPlanAsync(
        PlannerDbContext context, PlanGraphEntity plan, List<PlanRunEntity> runs, CancellationToken cancellationToken)
    {
        var steps = await context.PlanSteps.AsNoTracking()
            .Where(s => s.PlanGraphId == plan.Id)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);
        var stepIds = steps.Select(s => s.Id).ToList();
        var states = await context.StepExecutionStates.AsNoTracking()
            .Where(s => stepIds.Contains(s.StepId))
            .ToDictionaryAsync(s => s.StepId, cancellationToken);
        var edges = await context.PlanEdges.AsNoTracking()
            .Where(e => e.PlanGraphId == plan.Id)
            .Select(e => new ExportedEdge(e.Id, e.FromStepId, e.ToStepId, e.Type.ToString(), e.Condition))
            .ToListAsync(cancellationToken);
        var log = await context.PlanExecutionLogs.AsNoTracking()
            .Where(l => l.PlanGraphId == plan.Id)
            .OrderBy(l => l.Id)
            .Select(l => new ExportedLogEntry(l.Timestamp, l.StepId, l.EventType, l.DetailsJson))
            .ToListAsync(cancellationToken);

        return new ExportedPlan(
            plan.Id, plan.Name, plan.ParentPlanId, plan.ConfigurationJson, plan.CreatedAt, plan.UpdatedAt, plan.TenantId,
            [.. steps.Select(s => ToStep(s, states.GetValueOrDefault(s.Id)))],
            edges,
            log,
            [.. runs.Where(r => r.PlanGraphId == plan.Id).Select(ToRun)]);
    }

    private static ExportedStep ToStep(PlanStepEntity step, StepExecutionStateEntity? state) => new(
        step.Id, step.Name, step.Type.ToString(), step.ConfigurationJson, step.RetryPolicyJson, step.TimeoutSeconds,
        step.RequiredAutonomyLevel?.ToString(),
        state is null ? null : new ExportedStepState(
            state.Status.ToString(), state.AttemptCount, state.StartedAt, state.CompletedAt, state.Output,
            state.ErrorMessage, state.AttestationJson));

    private static ExportedRun ToRun(PlanRunEntity run) =>
        new(run.PlanGraphId, run.RunScope, run.ToolResultScopeId, run.LastRunAt, run.IsRunning);

    private sealed record ExportedPlan(
        Guid Id, string Name, Guid? ParentPlanId, string ConfigurationJson, DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt, string? TenantId, IReadOnlyList<ExportedStep> Steps, IReadOnlyList<ExportedEdge> Edges,
        IReadOnlyList<ExportedLogEntry> ExecutionLog, IReadOnlyList<ExportedRun> Runs);

    private sealed record ExportedStep(
        Guid Id, string Name, string Type, string ConfigurationJson, string RetryPolicyJson, double TimeoutSeconds,
        string? RequiredAutonomyLevel, ExportedStepState? State);

    private sealed record ExportedStepState(
        string Status, int AttemptCount, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Output,
        string? ErrorMessage, string? AttestationJson);

    private sealed record ExportedEdge(Guid Id, Guid FromStepId, Guid ToStepId, string Type, string? Condition);

    private sealed record ExportedLogEntry(DateTimeOffset Timestamp, Guid? StepId, string EventType, string? DetailsJson);

    private sealed record ExportedRun(
        Guid PlanId, string RunScope, string ToolResultScopeId, DateTimeOffset LastRunAt, bool IsRunning);

    /// <summary>A run the person made on a plan someone else owns; that plan is not part of their copy.</summary>
    private sealed record ExportedRunOnAnotherPlan(ExportedRun Run);
}
