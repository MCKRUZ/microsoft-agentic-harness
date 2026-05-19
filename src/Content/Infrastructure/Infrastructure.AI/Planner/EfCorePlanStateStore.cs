using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
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

    public EfCorePlanStateStore(
        IDbContextFactory<PlannerDbContext> factory,
        ILogger<EfCorePlanStateStore> logger,
        TimeProvider timeProvider)
    {
        _factory = factory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<Result> SavePlanAsync(PlanGraph plan, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();
        var now = _timeProvider.GetUtcNow();

        var graphEntity = new PlanGraphEntity
        {
            Id = plan.Id.Value,
            Name = plan.Name,
            ParentPlanId = plan.ParentPlanId?.Value,
            ConfigurationJson = JsonSerializer.Serialize(plan.Configuration, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
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
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("Saved plan {PlanId} with {StepCount} steps", plan.Id.Value, plan.Steps.Count);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> UpdateStepStateAsync(StepExecutionState state, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

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
