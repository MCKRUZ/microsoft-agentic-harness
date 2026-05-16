using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PlanEdgeEntity"/>. Uses <c>DeleteBehavior.Restrict</c>
/// on step FKs to prevent accidental cascade deletes through edge references, and defines
/// a composite index for efficient graph traversal queries.
/// </summary>
public sealed class PlanEdgeEntityConfiguration : IEntityTypeConfiguration<PlanEdgeEntity>
{
    public void Configure(EntityTypeBuilder<PlanEdgeEntity> builder)
    {
        builder.ToTable("PlanEdges");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Type).HasConversion<string>();

        builder.HasOne(e => e.PlanGraph)
            .WithMany(e => e.Edges)
            .HasForeignKey(e => e.PlanGraphId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict delete on step FKs — edges should be explicitly removed
        builder.HasOne<PlanStepEntity>()
            .WithMany()
            .HasForeignKey(e => e.FromStepId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PlanStepEntity>()
            .WithMany()
            .HasForeignKey(e => e.ToStepId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.PlanGraphId, e.FromStepId, e.ToStepId, e.Type }).IsUnique();
        builder.HasIndex(e => new { e.PlanGraphId, e.ToStepId });
    }
}
