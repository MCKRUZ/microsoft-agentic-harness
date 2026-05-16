using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PlanStepEntity"/>. Stores <c>StepType</c>
/// as a string, keeps <c>ConfigurationJson</c> as a plain string column (polymorphic
/// JSON handled by the state store), and defines the one-to-one relationship
/// with <see cref="StepExecutionStateEntity"/>.
/// </summary>
public sealed class PlanStepEntityConfiguration : IEntityTypeConfiguration<PlanStepEntity>
{
    public void Configure(EntityTypeBuilder<PlanStepEntity> builder)
    {
        builder.ToTable("PlanSteps");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ConfigurationJson).IsRequired();
        builder.Property(e => e.RetryPolicyJson).IsRequired();

        builder.Property(e => e.Type).HasConversion<string>();
        builder.Property(e => e.RequiredAutonomyLevel).HasConversion<string>();

        builder.HasOne(e => e.PlanGraph)
            .WithMany(e => e.Steps)
            .HasForeignKey(e => e.PlanGraphId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.ExecutionState)
            .WithOne(e => e.Step)
            .HasForeignKey<StepExecutionStateEntity>(e => e.StepId);
    }
}
