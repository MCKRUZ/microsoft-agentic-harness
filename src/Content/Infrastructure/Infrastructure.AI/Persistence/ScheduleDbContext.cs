using Infrastructure.AI.Persistence.Configurations;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core DbContext for the recurring-schedule seam (#593): cron-driven, timezone-aware
/// definitions that enqueue a run on a clock. Targets SQLite.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PlannerDbContext"/> (plan execution state) and
/// <c>GovernanceStateDbContext</c>, matching the repo's one-context-per-subsystem convention — a
/// schedule is not plan-execution state, and it binds its own <c>AppConfig:AI:Schedules</c>
/// section rather than <c>AI:Planner</c>. Configured inline via
/// <see cref="ScheduleEntityConfiguration"/> rather than through <see cref="PlannerDbContext"/>'s
/// assembly-scan-avoidance mechanism, since this context owns exactly one entity and has no
/// sibling configurations to accidentally pull in.
/// </remarks>
[PersonalDataChannel("The schedules SQLite database; the schedule store describes its table.")]
public sealed class ScheduleDbContext : DbContext
{
    /// <summary>Recurring schedules.</summary>
    public DbSet<ScheduleEntity> Schedules => Set<ScheduleEntity>();

    public ScheduleDbContext(DbContextOptions<ScheduleDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ScheduleEntityConfiguration());
    }
}
