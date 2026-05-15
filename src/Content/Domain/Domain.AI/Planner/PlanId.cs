namespace Domain.AI.Planner;

/// <summary>
/// Strongly-typed identifier for a <see cref="PlanGraph"/>.
/// Wraps a <see cref="Guid"/> to prevent primitive obsession and accidental
/// misuse of raw GUIDs across different entity boundaries.
/// </summary>
public readonly record struct PlanId(Guid Value)
{
    /// <summary>Generates a new unique <see cref="PlanId"/>.</summary>
    public static PlanId New() => new(Guid.NewGuid());
}
