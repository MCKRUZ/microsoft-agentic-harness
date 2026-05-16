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

        builder.HasIndex(e => e.ParentPlanId);
        builder.HasIndex(e => e.CreatedAt);
    }
}
