using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PlanGraphEntity"/>. Defines primary key,
/// concurrency token, self-referencing FK for sub-plans, and indexes.
/// </summary>
public sealed class PlanGraphEntityConfiguration : IEntityTypeConfiguration<PlanGraphEntity>
{
    public void Configure(EntityTypeBuilder<PlanGraphEntity> builder)
    {
        builder.ToTable("PlanGraphs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.ConfigurationJson).IsRequired();

        builder.Property(e => e.Version).IsConcurrencyToken();

        // Self-referencing FK for sub-plans
        builder.HasOne(e => e.ParentPlan)
            .WithMany(e => e.ChildPlans)
            .HasForeignKey(e => e.ParentPlanId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(e => e.OwnerId).HasMaxLength(PlannerScopeFilter.MaxIdentityLength);
        builder.Property(e => e.TenantId).HasMaxLength(PlannerScopeFilter.MaxIdentityLength);

        builder.HasIndex(e => e.ParentPlanId);
        builder.HasIndex(e => e.CreatedAt);

        // Serves exact-match owner/tenant lookups — the write-path predicate
        // (PlannerScopeFilter.WritableBy: TenantId = ? AND OwnerId = ?) can seek it directly.
        // The read-path visibility predicate's OR-under-AND shape (null-or-match per column)
        // is not sargable against this index on SQLite; reads scan, which is acceptable at
        // planner-table cardinality.
        builder.HasIndex(e => new { e.TenantId, e.OwnerId });
    }
}
