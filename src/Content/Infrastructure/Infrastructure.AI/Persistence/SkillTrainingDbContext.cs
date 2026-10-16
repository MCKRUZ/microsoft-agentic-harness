using Infrastructure.AI.Persistence.Configurations;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// SQLite store for skill-training checkpoints and run leases (#853).
/// </summary>
/// <remarks>
/// Its own database file, like the planner's and the scheduler's: a subsystem's data has its own lifecycle
/// (a training run's checkpoints are worth keeping or deleting independently of a plan's), and a separate
/// file lets each be backed up, pruned or moved without touching the others.
/// </remarks>
public sealed class SkillTrainingDbContext : DbContext
{
    /// <summary>The longest run id stored.</summary>
    public const int MaxRunIdLength = 256;

    /// <summary>The stored checkpoints.</summary>
    public DbSet<SkillTrainingCheckpointEntity> Checkpoints => Set<SkillTrainingCheckpointEntity>();

    /// <summary>The claims on runs.</summary>
    public DbSet<SkillTrainingRunLeaseEntity> RunLeases => Set<SkillTrainingRunLeaseEntity>();

    /// <summary>Initializes the context.</summary>
    public SkillTrainingDbContext(DbContextOptions<SkillTrainingDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SkillTrainingCheckpointEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SkillTrainingRunLeaseEntityConfiguration());
    }
}
