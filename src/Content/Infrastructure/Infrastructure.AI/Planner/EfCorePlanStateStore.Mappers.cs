using System.Text.Json;
using Domain.AI.Attestation;
using Domain.AI.Planner;
using Infrastructure.AI.Persistence.Entities;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore
{
    private static PlanGraph MapToDomain(PlanGraphEntity entity)
    {
        var steps = entity.Steps.OrderBy(s => s.Name).Select(s => new PlanStep
        {
            Id = new PlanStepId(s.Id),
            Name = s.Name,
            Type = s.Type,
            Configuration = JsonSerializer.Deserialize<StepConfiguration>(s.ConfigurationJson, JsonOptions)!,
            RetryPolicy = JsonSerializer.Deserialize<RetryPolicy>(s.RetryPolicyJson, JsonOptions)!,
            Timeout = TimeSpan.FromSeconds(s.TimeoutSeconds),
            RequiredAutonomyLevel = s.RequiredAutonomyLevel,
        }).ToList();

        var edges = entity.Edges.Select(e => new PlanEdge(
            new PlanStepId(e.FromStepId),
            new PlanStepId(e.ToStepId),
            e.Type,
            e.Condition)).ToList();

        return new PlanGraph
        {
            Id = new PlanId(entity.Id),
            Name = entity.Name,
            Steps = steps,
            Edges = edges,
            Configuration = JsonSerializer.Deserialize<PlanConfiguration>(entity.ConfigurationJson, JsonOptions)!,
            ParentPlanId = entity.ParentPlanId.HasValue ? new PlanId(entity.ParentPlanId.Value) : null,
        };
    }

    private static StepExecutionState MapToStepState(StepExecutionStateEntity e) => new()
    {
        StepId = new PlanStepId(e.StepId),
        Status = e.Status,
        AttemptCount = e.AttemptCount,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        Output = e.Output,
        ErrorMessage = e.ErrorMessage,
        Attestation = DeserializeAttestation(e.AttestationJson),
    };

    private static Dictionary<PlanStepId, StepExecutionState> MapToStepStateDictionary(
        IEnumerable<StepExecutionStateEntity> entities)
        => entities.ToDictionary(e => new PlanStepId(e.StepId), MapToStepState);

    private static ToolExecutionAttestation? DeserializeAttestation(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ToolExecutionAttestation>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
