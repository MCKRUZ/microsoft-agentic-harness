using System.Text.Json;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore
{
    /// <inheritdoc />
    public async Task<Result<PlanGraph?>> LoadPlanAsync(PlanId planId, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        var entity = await ctx.PlanGraphs
            .AsNoTracking()
            .Include(g => g.Steps).ThenInclude(s => s.ExecutionState)
            .Include(g => g.Edges)
            .FirstOrDefaultAsync(g => g.Id == planId.Value, ct);

        if (entity is null)
            return Result<PlanGraph?>.Success(null);

        return Result<PlanGraph?>.Success(MapToDomain(entity));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>> LoadStepStatesAsync(
        PlanId planId, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        var entities = await ctx.StepExecutionStates
            .AsNoTracking()
            .Where(s => ctx.PlanSteps.Any(ps => ps.Id == s.StepId && ps.PlanGraphId == planId.Value))
            .ToListAsync(ct);

        if (entities.Count == 0)
            return Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
                new Dictionary<PlanStepId, StepExecutionState>());

        return Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(
            MapToStepStateDictionary(entities));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PlanGraph>>> ListPlansAsync(
        StepExecutionStatus? statusFilter,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        IQueryable<PlanGraphEntity> query = ctx.PlanGraphs
            .AsNoTracking()
            .Include(g => g.Steps).ThenInclude(s => s.ExecutionState)
            .Include(g => g.Edges);

        if (from.HasValue)
            query = query.Where(g => g.CreatedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(g => g.CreatedAt <= to.Value);

        if (statusFilter.HasValue)
            query = query.Where(g => g.Steps.Any(s => s.ExecutionState != null && s.ExecutionState.Status == statusFilter.Value));

        var entities = await query
            .Take(100)
            .ToListAsync(ct);

        var plans = entities
            .OrderByDescending(e => e.CreatedAt)
            .Select(MapToDomain)
            .ToList();
        return Result<IReadOnlyList<PlanGraph>>.Success(plans);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PlanExecutionLogEntry>>> GetExecutionHistoryAsync(
        PlanId planId, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        var logs = await ctx.PlanExecutionLogs
            .AsNoTracking()
            .Where(l => l.PlanGraphId == planId.Value && l.StepId != null)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        var entries = new List<PlanExecutionLogEntry>(logs.Count);
        foreach (var log in logs)
        {
            if (!Enum.TryParse<StepExecutionStatus>(log.EventType, out var status))
                continue;

            var attemptNumber = 1;
            string? message = null;
            if (log.DetailsJson is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(log.DetailsJson);
                    if (doc.RootElement.TryGetProperty("attemptCount", out var ac))
                        attemptNumber = ac.GetInt32();
                    if (doc.RootElement.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String)
                        message = o.GetString();
                    if (message is null && doc.RootElement.TryGetProperty("errorMessage", out var em) && em.ValueKind == JsonValueKind.String)
                        message = em.GetString();
                }
                catch (JsonException)
                {
                    // Malformed details — skip enrichment
                }
            }

            entries.Add(new PlanExecutionLogEntry
            {
                PlanId = planId,
                StepId = new PlanStepId(log.StepId!.Value),
                Timestamp = log.Timestamp,
                Status = status,
                Message = message,
                AttemptNumber = attemptNumber,
            });
        }

        return Result<IReadOnlyList<PlanExecutionLogEntry>>.Success(entries);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>> ResumeAsync(
        PlanId planId, CancellationToken ct)
    {
        await using var ctx = _factory.CreateDbContext();

        var entities = await ctx.StepExecutionStates
            .Where(s => ctx.PlanSteps.Any(ps => ps.Id == s.StepId && ps.PlanGraphId == planId.Value))
            .ToListAsync(ct);

        if (entities.Count == 0)
            return Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.NotFound(
                $"No step states found for plan {planId.Value}");

        foreach (var entity in entities.Where(e => e.Status == StepExecutionStatus.Running))
        {
            entity.Status = StepExecutionStatus.Ready;
            // Version incremented by SqliteVersionInterceptor on save
        }

        ctx.PlanExecutionLogs.Add(new PlanExecutionLogEntity
        {
            PlanGraphId = planId.Value,
            EventType = "resumed",
            Timestamp = _timeProvider.GetUtcNow(),
        });

        await ctx.SaveChangesAsync(ct);

        var stateMap = MapToStepStateDictionary(entities);

        _logger.LogInformation("Resumed plan {PlanId}, {StateCount} step states loaded", planId.Value, stateMap.Count);
        return Result<IReadOnlyDictionary<PlanStepId, StepExecutionState>>.Success(stateMap);
    }
}
