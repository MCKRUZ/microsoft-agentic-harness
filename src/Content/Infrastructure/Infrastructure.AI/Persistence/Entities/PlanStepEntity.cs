using Domain.AI.Planner;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity representing a <see cref="PlanStep"/>. Stores the polymorphic
/// <see cref="StepConfiguration"/> and <see cref="RetryPolicy"/> as JSON columns
/// with type discriminators handled by the state store layer.
/// </summary>
public sealed class PlanStepEntity
{
    /// <summary>Maps from <see cref="PlanStepId.Value"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>Foreign key to the owning <see cref="PlanGraphEntity"/>.</summary>
    public Guid PlanGraphId { get; set; }

    /// <summary>Human-readable name describing the step's purpose.</summary>
    public required string Name { get; set; }

    /// <summary>Step type stored as a string for readability and resilience.</summary>
    public StepType Type { get; set; }

    /// <summary>Polymorphic JSON with <c>type</c> discriminator for <see cref="StepConfiguration"/>.</summary>
    public required string ConfigurationJson { get; set; }

    /// <summary>Serialized <see cref="RetryPolicy"/>.</summary>
    public required string RetryPolicyJson { get; set; }

    /// <summary>Step timeout stored as seconds (maps from <see cref="TimeSpan"/>).</summary>
    public double TimeoutSeconds { get; set; }

    /// <summary>Minimum autonomy level required. Null means no additional check.</summary>
    public Domain.AI.Governance.AutonomyLevel? RequiredAutonomyLevel { get; set; }

    // Navigation properties

    /// <summary>Owning plan graph.</summary>
    public PlanGraphEntity? PlanGraph { get; set; }

    /// <summary>One-to-one execution state for this step.</summary>
    public StepExecutionStateEntity? ExecutionState { get; set; }
}
