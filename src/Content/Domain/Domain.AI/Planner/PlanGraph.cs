namespace Domain.AI.Planner;

/// <summary>
/// The central domain model representing a directed acyclic graph of executable plan steps.
/// Steps are connected by edges defining data flow, control flow, and conditional branching.
/// </summary>
public sealed record PlanGraph
{
    /// <summary>Unique identifier for this plan.</summary>
    public required PlanId Id { get; init; }

    /// <summary>Human-readable name describing the plan's purpose.</summary>
    public required string Name { get; init; }

    /// <summary>Ordered collection of steps in this plan.</summary>
    public required IReadOnlyList<PlanStep> Steps { get; init; }

    /// <summary>Directed edges connecting steps in the plan graph.</summary>
    public required IReadOnlyList<PlanEdge> Edges { get; init; }

    /// <summary>Plan-level execution settings (timeouts, parallelism, recursion depth).</summary>
    public required PlanConfiguration Configuration { get; init; }

    /// <summary>
    /// Parent plan identifier for sub-plan invocations. Null for top-level plans.
    /// </summary>
    public PlanId? ParentPlanId { get; init; }
}
