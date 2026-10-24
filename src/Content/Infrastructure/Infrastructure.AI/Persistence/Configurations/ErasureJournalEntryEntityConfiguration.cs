using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>Maps <see cref="ErasureJournalEntryEntity"/> to the <c>ErasureJournal</c> table.</summary>
public sealed class ErasureJournalEntryEntityConfiguration : IEntityTypeConfiguration<ErasureJournalEntryEntity>
{
    /// <summary>The table name.</summary>
    public const string Table = "ErasureJournal";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ErasureJournalEntryEntity> builder)
    {
        builder.ToTable(Table);

        builder.HasKey(e => new { e.OwnerId, e.Kind, e.Value });
        builder.Property(e => e.OwnerId).HasMaxLength(256);
        builder.Property(e => e.Kind).HasMaxLength(32);
        builder.Property(e => e.Value).HasMaxLength(512);
        builder.Property(e => e.RequestId).IsRequired().HasMaxLength(64);

        // Stored as UTC ticks so SQLite can compare and order it (see SqliteValueConverters).
        builder.Property(e => e.RecordedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);
        builder.HasIndex(e => e.RecordedAt);

        // The conversation stores ask, on every new conversation, whether another person's erasure holds its id.
        builder.HasIndex(e => e.Value);
    }
}
