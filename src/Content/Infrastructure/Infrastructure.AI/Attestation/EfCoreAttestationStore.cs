using System.Text.Json;
using Application.AI.Common.Interfaces.Attestation;
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
public sealed class EfCoreAttestationStore : IAttestationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IDbContextFactory<PlannerDbContext> _factory;
    private readonly ILogger<EfCoreAttestationStore> _logger;

    public EfCoreAttestationStore(
        IDbContextFactory<PlannerDbContext> factory,
        ILogger<EfCoreAttestationStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(PlanStepId stepId, ToolExecutionAttestation attestation, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var entity = await context.StepExecutionStates
            .FirstOrDefaultAsync(s => s.StepId == stepId.Value, ct);

        if (entity is null)
        {
            _logger.LogWarning("StepExecutionState not found for step {StepId}", stepId.Value);
            return Result.Fail($"StepExecutionState not found for step {stepId.Value}");
        }

        entity.AttestationJson = JsonSerializer.Serialize(attestation, JsonOptions);
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ToolExecutionAttestation?>> GetByStepAsync(PlanStepId stepId, CancellationToken ct)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

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
