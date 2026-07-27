using System.Text.Json;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Attestation;
using Domain.AI.Planner;
using Domain.Common;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Attestation;

/// <summary>
/// EF Core implementation of <see cref="IAttestationStore"/> that persists attestations
/// as JSON columns on <see cref="Persistence.Entities.StepExecutionStateEntity"/>.
/// Uses <see cref="IDbContextFactory{TContext}"/> for singleton-safe context creation.
/// </summary>
/// <remarks>
/// Attestations hang off plan steps, so this store enforces the same plan-ownership
/// boundaries as <c>EfCorePlanStateStore</c> via the shared <see cref="PlannerScopeFilter"/>
/// predicates: reads are gated by plan VISIBILITY (shared semantics — global plans readable
/// by all), writes by strict WRITABILITY (exact owner+tenant match). Cross-owner access is
/// indistinguishable from absence — null/empty/NotFound, never Forbidden (404-not-403).
/// </remarks>
public sealed class EfCoreAttestationStore : IAttestationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IDbContextFactory<PlannerDbContext> _factory;
    private readonly ILogger<EfCoreAttestationStore> _logger;
    private readonly IKnowledgeScope _scope;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfCoreAttestationStore"/> class.
    /// </summary>
    /// <param name="factory">Factory for short-lived planner contexts.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="scope">
    /// Ambient knowledge scope of the caller; drives plan visibility/writability gating.
    /// </param>
    public EfCoreAttestationStore(
        IDbContextFactory<PlannerDbContext> factory,
        ILogger<EfCoreAttestationStore> logger,
        IKnowledgeScope scope)
    {
        _factory = factory;
        _logger = logger;
        _scope = scope;
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(PlanStepId stepId, ToolExecutionAttestation attestation, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        // Write path: strict ownership — a step on a plan the caller doesn't own (including
        // globally READABLE null-owner plans) is indistinguishable from missing (404-not-403).
        if (!await PlannerScopeFilter.WritableBy(context.PlanGraphs, _scope)
                .AnyAsync(g => g.Steps.Any(s => s.Id == stepId.Value), ct))
            return Result.NotFound($"StepExecutionState not found for step {stepId.Value}");

        var entity = await context.StepExecutionStates
            .FirstOrDefaultAsync(s => s.StepId == stepId.Value, ct);

        if (entity is null)
        {
            _logger.LogWarning("StepExecutionState not found for step {StepId}", stepId.Value);
            return Result.NotFound($"StepExecutionState not found for step {stepId.Value}");
        }

        entity.AttestationJson = JsonSerializer.Serialize(attestation, JsonOptions);
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ToolExecutionAttestation?>> GetByStepAsync(PlanStepId stepId, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        // Read path: shared visibility — an attestation on another owner's plan resolves to
        // null, exactly like a missing one.
        if (!await PlannerScopeFilter.VisibleTo(context.PlanGraphs, _scope)
                .AnyAsync(g => g.Steps.Any(s => s.Id == stepId.Value), ct))
            return Result<ToolExecutionAttestation?>.Success(null);

        var entity = await context.StepExecutionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StepId == stepId.Value, ct);

        if (entity is null)
            return Result<ToolExecutionAttestation?>.Success(null);

        if (string.IsNullOrEmpty(entity.AttestationJson))
            return Result<ToolExecutionAttestation?>.Success(null);

        var attestation = JsonSerializer.Deserialize<ToolExecutionAttestation>(entity.AttestationJson, JsonOptions);
        return Result<ToolExecutionAttestation?>.Success(attestation);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ToolExecutionAttestation>>> GetByPlanAsync(PlanId planId, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        // Read path: shared visibility — another owner's plan yields the same empty list as
        // a missing plan.
        if (!await PlannerScopeFilter.VisibleTo(context.PlanGraphs, _scope)
                .AnyAsync(g => g.Id == planId.Value, ct))
            return Result<IReadOnlyList<ToolExecutionAttestation>>.Success([]);

        var entities = await context.StepExecutionStates
            .AsNoTracking()
            .Where(s => s.Step != null && s.Step.PlanGraphId == planId.Value)
            .Where(s => s.AttestationJson != null)
            .ToListAsync(ct);

        var attestations = entities
            .Select(e => JsonSerializer.Deserialize<ToolExecutionAttestation>(e.AttestationJson!, JsonOptions))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        return Result<IReadOnlyList<ToolExecutionAttestation>>.Success(attestations);
    }
}
