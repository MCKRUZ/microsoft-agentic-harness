namespace Domain.AI.Planner;

/// <summary>
/// Strongly-typed identifier for a <see cref="PlanStep"/>.
/// Wraps a <see cref="Guid"/> to prevent primitive obsession.
/// </summary>
public readonly record struct PlanStepId(Guid Value)
{
    /// <summary>Generates a new unique <see cref="PlanStepId"/>.</summary>
    public static PlanStepId New() => new(Guid.NewGuid());
}
