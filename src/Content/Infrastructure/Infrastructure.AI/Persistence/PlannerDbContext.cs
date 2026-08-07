using Infrastructure.AI.Persistence.Configurations;
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

    /// <summary>
    /// Declares the planner's model explicitly, one configuration at a time.
    /// </summary>
    /// <remarks>
    /// This deliberately does <b>not</b> scan the assembly. Infrastructure.AI hosts several
    /// unrelated databases, and a scan pulls in every <see cref="IEntityTypeConfiguration{TEntity}"/>
    /// it finds — so any configuration added anywhere in this assembly, for any other subsystem,
    /// would silently acquire a table in the planner's database. The other contexts here already
    /// route around the scan by configuring themselves inline; the planner declares its five.
    /// <c>PlannerDbContext_Model_ContainsOnlyPlannerEntities</c> fails if that stops being true.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PlanGraphEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PlanStepEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PlanEdgeEntityConfiguration());
        modelBuilder.ApplyConfiguration(new StepExecutionStateEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PlanExecutionLogEntityConfiguration());
    }
}
