using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core DbContext for the planner subsystem. Manages plan graphs, steps,
/// edges, execution state, and audit logs. Targets SQLite with WAL mode
/// for read concurrency and stores enums as strings for resilience.
/// </summary>
public sealed class PlannerDbContext : DbContext
{
    /// <summary>Plan graph roots.</summary>
    public DbSet<PlanGraphEntity> PlanGraphs => Set<PlanGraphEntity>();

    /// <summary>Individual plan steps.</summary>
    public DbSet<PlanStepEntity> PlanSteps => Set<PlanStepEntity>();

    /// <summary>Directed edges between plan steps.</summary>
    public DbSet<PlanEdgeEntity> PlanEdges => Set<PlanEdgeEntity>();

    /// <summary>Step-level execution state with concurrency tokens.</summary>
    public DbSet<StepExecutionStateEntity> StepExecutionStates => Set<StepExecutionStateEntity>();

    /// <summary>Append-only plan execution audit log.</summary>
    public DbSet<PlanExecutionLogEntity> PlanExecutionLogs => Set<PlanExecutionLogEntity>();

    public PlannerDbContext(DbContextOptions<PlannerDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlannerDbContext).Assembly);
    }
}
