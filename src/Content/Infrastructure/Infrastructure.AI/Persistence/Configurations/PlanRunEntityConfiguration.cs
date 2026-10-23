using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PlanRunEntity"/>: deleted with its plan, unique per plan, owner and scopes so a
/// repeated run refreshes its row, and indexed by owner for erasure.
/// </summary>
public sealed class PlanRunEntityConfiguration : IEntityTypeConfiguration<PlanRunEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PlanRunEntity> builder)
    {
        builder.ToTable("PlanRuns");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.OwnerId).IsRequired().HasMaxLength(PlannerScopeFilter.MaxIdentityLength);
        builder.Property(e => e.RunScope).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ToolResultScopeId).IsRequired().HasMaxLength(256);

        // Ticks, so the pruning comparison and the upsert's MAX run in SQL.
        builder.Property(e => e.LastRunAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        builder.HasOne(e => e.PlanGraph)
            .WithMany(e => e.Runs)
            .HasForeignKey(e => e.PlanGraphId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.PlanGraphId, e.OwnerId, e.RunScope, e.ToolResultScopeId }).IsUnique();
        builder.HasIndex(e => e.OwnerId);
    }
}
