using Domain.AI.Planner;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity representing a <see cref="PlanEdge"/>. Unlike the domain
/// record (which has no Id), the entity has an auto-generated primary key
/// and foreign keys to both source and target steps.
/// </summary>
public sealed class PlanEdgeEntity
{
    /// <summary>Auto-generated primary key (not present in domain model).</summary>
    public Guid Id { get; set; }

    /// <summary>Foreign key to the owning <see cref="PlanGraphEntity"/>.</summary>
    public Guid PlanGraphId { get; set; }

    /// <summary>Source step identifier.</summary>
    public Guid FromStepId { get; set; }

    /// <summary>Target step identifier.</summary>
    public Guid ToStepId { get; set; }

    /// <summary>Edge type stored as a string for readability and resilience.</summary>
    public EdgeType Type { get; set; }

    /// <summary>Optional condition expression for conditional edges.</summary>
    public string? Condition { get; set; }

    // Navigation properties

    /// <summary>Owning plan graph.</summary>
    public PlanGraphEntity? PlanGraph { get; set; }
}
