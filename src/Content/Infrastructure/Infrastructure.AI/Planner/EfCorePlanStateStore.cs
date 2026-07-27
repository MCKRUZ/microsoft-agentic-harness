using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// EF Core implementation of <see cref="IPlanStateStore"/> that bridges planner domain
/// operations to the persistence layer. Uses <see cref="IDbContextFactory{TContext}"/>
/// for short-lived contexts, making it safe for singleton and scoped callers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership:</b> every saved plan is stamped with the canonicalized owner/tenant from
/// the ambient <see cref="IKnowledgeScope"/> (set per-request by the host's scope
/// middleware/hub filter and flowing via <c>AsyncLocal</c>), mirroring how
/// <c>KnowledgeMemoryService</c> self-stamps knowledge records. Identity is never accepted
/// from a command or DTO — ambient scope only. Every read/list/execute path filters by the
/// same scope; a plan another owner saved is indistinguishable from a missing plan
/// (NotFound/null/empty, never Forbidden).
/// </para>
/// <para>
/// <b>Schema:</b> the constructor demands <see cref="SchemaInitializer{TContext}"/> so
/// resolving the store forces the SQLite schema into existence before the first operation —
/// the same lifecycle the prompt-usage and eval-dashboard stores use, expressed as a plain
/// constructor dependency so it stays visible to <c>ValidateOnBuild</c>.
/// </para>
/// </remarks>
public sealed partial class EfCorePlanStateStore : IPlanStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDbContextFactory<PlannerDbContext> _factory;
    private readonly ILogger<EfCorePlanStateStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IKnowledgeScope _scope;

    public EfCorePlanStateStore(
        IDbContextFactory<PlannerDbContext> factory,
        ILogger<EfCorePlanStateStore> logger,
        TimeProvider timeProvider,
        IKnowledgeScope scope,
        SchemaInitializer<PlannerDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        _factory = factory;
        _logger = logger;
        _timeProvider = timeProvider;
        _scope = scope;
    }

    /// <inheritdoc />
    public async Task<Result> SavePlanAsync(PlanGraph plan, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();
        var now = _timeProvider.GetUtcNow();

        // Self-stamped from the ambient scope (never from caller-supplied data) in the
        // shared canonical form, so scope filters compare identically on read/write.
        var owner = ScopeIdentity.Canonicalize(_scope.UserId);
        var tenant = ScopeIdentity.Canonicalize(_scope.TenantId);

        // SQLite treats HasMaxLength as advisory; guard explicitly so an oversized identity
        // fails cleanly here instead of silently truncating on providers that would.
        if (owner?.Length > PlannerScopeFilter.MaxIdentityLength
            || tenant?.Length > PlannerScopeFilter.MaxIdentityLength)
            return Result.Fail("Ambient scope identity exceeds the maximum supported length.");

        // Plan ids are caller-supplied; a colliding id must not surface as an unhandled
        // constraint exception. The probe is scope-filtered so a collision with a plan the
        // caller cannot see never confirms its existence — that case falls through to the
        // insert and surfaces as the same generic constraint failure as any other race.
        if (await VisiblePlans(ctx).AsNoTracking().AnyAsync(g => g.Id == plan.Id.Value, ct))
            return Result.Fail("A plan with this id already exists.");

        var graphEntity = new PlanGraphEntity
        {
            Id = plan.Id.Value,
            Name = plan.Name,
            ParentPlanId = plan.ParentPlanId?.Value,
            ConfigurationJson = JsonSerializer.Serialize(plan.Configuration, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
            OwnerId = owner,
            TenantId = tenant,
        };

        foreach (var step in plan.Steps)
        {
            var stepEntity = new PlanStepEntity
            {
                Id = step.Id.Value,
                PlanGraphId = plan.Id.Value,
                Name = step.Name,
                Type = step.Type,
                ConfigurationJson = JsonSerializer.Serialize(step.Configuration, JsonOptions),
                RetryPolicyJson = JsonSerializer.Serialize(step.RetryPolicy, JsonOptions),
                TimeoutSeconds = step.Timeout.TotalSeconds,
                RequiredAutonomyLevel = step.RequiredAutonomyLevel,
                ExecutionState = new StepExecutionStateEntity
                {
                    Id = Guid.NewGuid(),
                    StepId = step.Id.Value,
                    Status = StepExecutionStatus.Pending,
                    AttemptCount = 0,
                },
            };
            graphEntity.Steps.Add(stepEntity);
        }

        foreach (var edge in plan.Edges)
        {
            graphEntity.Edges.Add(new PlanEdgeEntity
            {
                Id = Guid.NewGuid(),
                PlanGraphId = plan.Id.Value,
                FromStepId = edge.From.Value,
                ToStepId = edge.To.Value,
                Type = edge.Type,
                Condition = edge.Condition,
            });
        }

        graphEntity.ExecutionLogs.Add(new PlanExecutionLogEntity
        {
            PlanGraphId = plan.Id.Value,
            EventType = "plan_created",
            Timestamp = now,
        });

        ctx.PlanGraphs.Add(graphEntity);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent insert of the same id, or a collision with a plan outside the
            // caller's scope (deliberately not confirmed by the pre-check above). One
            // generic answer for both; full detail goes to structured logging only.
            _logger.LogWarning(ex, "Failed to persist plan {PlanId}: constraint violation", plan.Id.Value);
            return Result.Fail("Failed to persist the plan.");
        }

        _logger.LogInformation("Saved plan {PlanId} with {StepCount} steps", plan.Id.Value, plan.Steps.Count);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> UpdateStepStateAsync(StepExecutionState state, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        // Write path: strict ownership — a step on a plan the caller doesn't own (including
        // globally READABLE null-owner plans) is indistinguishable from missing (404-not-403).
        // The warning keeps a mis-wired scope (e.g. tenant fallback changed between save and
        // update) diagnosable without weakening the outward NotFound.
        if (!await IsStepWritableAsync(ctx, state.StepId.Value, ct))
        {
            _logger.LogWarning(
                "Step {StepId} is missing or not writable in the current scope; update rejected",
                state.StepId.Value);
            return Result.NotFound($"Execution state not found for step {state.StepId.Value}");
        }

        var entity = await ctx.StepExecutionStates
            .FirstOrDefaultAsync(s => s.StepId == state.StepId.Value, ct);

        if (entity is null)
            return Result.NotFound($"Execution state not found for step {state.StepId.Value}");

        entity.Status = state.Status;
        entity.AttemptCount = state.AttemptCount;
        entity.StartedAt = state.StartedAt;
        entity.CompletedAt = state.CompletedAt;
        entity.Output = state.Output;
        entity.ErrorMessage = state.ErrorMessage;
        entity.AttestationJson = state.Attestation is not null
            ? JsonSerializer.Serialize(state.Attestation, JsonOptions)
            : null;
        // Version incremented by SqliteVersionInterceptor on save

        var step = await ctx.PlanSteps
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == state.StepId.Value, ct);

        if (step is not null)
        {
            ctx.PlanExecutionLogs.Add(new PlanExecutionLogEntity
            {
                PlanGraphId = step.PlanGraphId,
                StepId = state.StepId.Value,
                EventType = state.Status.ToString(),
                Timestamp = _timeProvider.GetUtcNow(),
                DetailsJson = JsonSerializer.Serialize(new { state.AttemptCount, state.Output, state.ErrorMessage }, JsonOptions),
            });
        }

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail($"Concurrency conflict updating step {state.StepId.Value}. The step was modified by another operation.");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CheckpointAsync(
        PlanId planId, IReadOnlyList<StepExecutionState> states, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        // Write path: strict ownership — a plan the caller doesn't own (including globally
        // READABLE null-owner plans) is indistinguishable from missing (404-not-403).
        // The warning keeps a mis-wired scope diagnosable without weakening the NotFound.
        if (!await IsPlanWritableAsync(ctx, planId.Value, ct))
        {
            _logger.LogWarning(
                "Plan {PlanId} is missing or not writable in the current scope; checkpoint rejected",
                planId.Value);
            return Result.NotFound($"No step states found for plan {planId.Value}");
        }

        var entities = await ctx.StepExecutionStates
            .Where(s => ctx.PlanSteps.Any(ps => ps.Id == s.StepId && ps.PlanGraphId == planId.Value))
            .ToListAsync(ct);

        if (entities.Count == 0)
            return Result.NotFound($"No step states found for plan {planId.Value}");

        var entityMap = entities.ToDictionary(e => e.StepId);

        var missingSteps = states
            .Where(s => !entityMap.ContainsKey(s.StepId.Value))
            .Select(s => s.StepId.Value)
            .ToList();

        if (missingSteps.Count > 0)
            return Result.Fail($"Checkpoint contains {missingSteps.Count} step(s) not found in plan {planId.Value}");

        foreach (var state in states)
        {
            var entity = entityMap[state.StepId.Value];

            entity.Status = state.Status;
            entity.AttemptCount = state.AttemptCount;
            entity.StartedAt = state.StartedAt;
            entity.CompletedAt = state.CompletedAt;
            entity.Output = state.Output;
            entity.ErrorMessage = state.ErrorMessage;
            entity.AttestationJson = state.Attestation is not null
                ? JsonSerializer.Serialize(state.Attestation, JsonOptions)
                : null;
            // Version incremented by SqliteVersionInterceptor on save
        }

        ctx.PlanExecutionLogs.Add(new PlanExecutionLogEntity
        {
            PlanGraphId = planId.Value,
            EventType = "checkpoint",
            Timestamp = _timeProvider.GetUtcNow(),
            DetailsJson = JsonSerializer.Serialize(new { StepCount = states.Count }, JsonOptions),
        });

        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("Checkpointed plan {PlanId} with {StateCount} step states", planId.Value, states.Count);
        return Result.Success();
    }
}
