using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>Maps <see cref="SkillTrainingCheckpointEntity"/> (#853).</summary>
public sealed class SkillTrainingCheckpointEntityConfiguration : IEntityTypeConfiguration<SkillTrainingCheckpointEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SkillTrainingCheckpointEntity> builder)
    {
        builder.ToTable("SkillTrainingCheckpoints");

        builder.HasKey(e => new { e.RunId, e.Step });
        builder.Property(e => e.RunId).IsRequired().HasMaxLength(SkillTrainingDbContext.MaxRunIdLength);
        builder.Property(e => e.Step).ValueGeneratedNever();
        builder.Property(e => e.PayloadJson).IsRequired();

        // Serves GetBestAsync: highest score in a run, latest step on a tie.
        builder.HasIndex(e => new { e.RunId, e.Score, e.Step });
    }
}

/// <summary>Maps <see cref="SkillTrainingRunLeaseEntity"/> (#853).</summary>
public sealed class SkillTrainingRunLeaseEntityConfiguration : IEntityTypeConfiguration<SkillTrainingRunLeaseEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SkillTrainingRunLeaseEntity> builder)
    {
        builder.ToTable("SkillTrainingRunLeases");

        builder.HasKey(e => e.RunId);
        builder.Property(e => e.RunId).IsRequired().HasMaxLength(SkillTrainingDbContext.MaxRunIdLength).ValueGeneratedNever();
        builder.Property(e => e.OwnerToken).IsRequired().HasMaxLength(64);
    }
}
