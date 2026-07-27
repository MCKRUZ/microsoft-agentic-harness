using Domain.AI.Planner;

namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity representing a <see cref="PlanGraph"/>. Stores plan-level
/// configuration as a JSON column and tracks optimistic concurrency via an
/// integer version token (SQLite lacks native rowversion).
/// </summary>
public sealed class PlanGraphEntity
{
    /// <summary>Maps from <see cref="PlanId.Value"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable name describing the plan's purpose.</summary>
    public required string Name { get; set; }

    /// <summary>Parent plan identifier for sub-plan invocations. Null for top-level plans.</summary>
    public Guid? ParentPlanId { get; set; }

    /// <summary>Serialized <see cref="PlanConfiguration"/>.</summary>
    public required string ConfigurationJson { get; set; }

    /// <summary>When this plan was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this plan was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token incremented on each save.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Canonicalized owner (user) identity stamped from the ambient knowledge scope at save
    /// time (see <see cref="Domain.AI.KnowledgeGraph.Scoping.ScopeIdentity"/>). Null denotes
    /// a global (unowned) plan, visible to every caller. Never accepted from a command/DTO —
    /// the state store stamps it itself.
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Canonicalized tenant identity stamped from the ambient knowledge scope at save time.
    /// Null denotes a global (tenant-unscoped) plan. Mirrors the knowledge-record visibility
    /// semantics used by the knowledge graph's tenant isolation.
    /// </summary>
    public string? TenantId { get; set; }

    // Navigation properties

    /// <summary>Steps belonging to this plan graph.</summary>
    public ICollection<PlanStepEntity> Steps { get; set; } = [];

    /// <summary>Edges connecting steps in this plan graph.</summary>
    public ICollection<PlanEdgeEntity> Edges { get; set; } = [];

    /// <summary>Append-only execution audit log entries.</summary>
    public ICollection<PlanExecutionLogEntity> ExecutionLogs { get; set; } = [];

    /// <summary>Self-referencing parent plan navigation.</summary>
    public PlanGraphEntity? ParentPlan { get; set; }

    /// <summary>Child sub-plans that reference this plan as their parent.</summary>
    public ICollection<PlanGraphEntity> ChildPlans { get; set; } = [];
}
