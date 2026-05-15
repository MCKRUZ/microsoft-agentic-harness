namespace Domain.AI.Planner;

/// <summary>
/// A directed edge between two <see cref="PlanStep"/> nodes in a <see cref="PlanGraph"/>.
/// Edges define data flow, control flow, and conditional branching relationships.
/// </summary>
/// <param name="From">The source step identifier.</param>
/// <param name="To">The target step identifier.</param>
/// <param name="Type">The relationship type between the connected steps.</param>
/// <param name="Condition">Optional condition expression for conditional edges. Null for non-conditional edges.</param>
public sealed record PlanEdge(
    PlanStepId From,
    PlanStepId To,
    EdgeType Type,
    string? Condition = null);
