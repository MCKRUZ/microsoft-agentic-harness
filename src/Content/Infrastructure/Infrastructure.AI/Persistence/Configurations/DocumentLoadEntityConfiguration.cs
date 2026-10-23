using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.AI.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="DocumentLoadEntity"/>: the document id as key, and the index the age-limit sweep's
/// oldest-first scan relies on.
/// </summary>
public sealed class DocumentLoadEntityConfiguration : IEntityTypeConfiguration<DocumentLoadEntity>
{
    /// <summary>
    /// The table name. The ledger's upsert names it literally (SQL text cannot take it as a parameter), so a rename must
    /// change both; the ledger tests exercise the upsert against this mapping.
    /// </summary>
    public const string Table = "DocumentLoads";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentLoadEntity> builder)
    {
        builder.ToTable(Table);

        builder.HasKey(e => e.DocumentId);
        builder.Property(e => e.DocumentId).ValueGeneratedNever().HasMaxLength(64);

        // Stored as UTC ticks so SQLite can compare and order it (see SqliteValueConverters).
        builder.Property(e => e.LastLoadedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);
        builder.HasIndex(e => e.LastLoadedAt);
    }
}
