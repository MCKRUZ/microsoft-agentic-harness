using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="StepExecutionStateEntity"/>. Enforces
/// a unique constraint on <c>StepId</c> (one-to-one with <see cref="PlanStepEntity"/>),
/// stores <c>Status</c> as a string, and includes a status index for ready-queue queries.
/// </summary>
public sealed class StepExecutionStateEntityConfiguration : IEntityTypeConfiguration<StepExecutionStateEntity>
{
    public void Configure(EntityTypeBuilder<StepExecutionStateEntity> builder)
    {
        builder.ToTable("StepExecutionStates");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Status).HasConversion<string>();
        builder.Property(e => e.Version).IsConcurrencyToken();

        builder.HasIndex(e => e.StepId).IsUnique();
        builder.HasIndex(e => e.Status);
    }
}
