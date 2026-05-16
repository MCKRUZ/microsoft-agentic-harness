using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PlanExecutionLogEntity"/>. Uses an auto-increment
/// long primary key and a composite index on <c>(PlanGraphId, Timestamp)</c> for
/// efficient chronological audit queries.
/// </summary>
public sealed class PlanExecutionLogEntityConfiguration : IEntityTypeConfiguration<PlanExecutionLogEntity>
{
    public void Configure(EntityTypeBuilder<PlanExecutionLogEntity> builder)
    {
        builder.ToTable("PlanExecutionLogs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.EventType).IsRequired().HasMaxLength(100);

        builder.HasOne(e => e.PlanGraph)
            .WithMany(e => e.ExecutionLogs)
            .HasForeignKey(e => e.PlanGraphId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.PlanGraphId, e.Timestamp });
    }
}
