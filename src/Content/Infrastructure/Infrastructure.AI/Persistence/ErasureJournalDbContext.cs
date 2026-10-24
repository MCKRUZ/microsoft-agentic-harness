using Domain.Common.Compliance;
using Infrastructure.AI.Persistence.Configurations;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>The erasure journal's SQLite database (#907).</summary>
[PersonalDataChannel("The erasure journal SQLite database; the erasure journal describes its table.")]
public sealed class ErasureJournalDbContext : DbContext
{
    /// <summary>The ids unfinished erasure requests read.</summary>
    public DbSet<ErasureJournalEntryEntity> Entries => Set<ErasureJournalEntryEntity>();

    /// <summary>Initializes a new instance of the <see cref="ErasureJournalDbContext"/> class.</summary>
    /// <param name="options">The context options.</param>
    public ErasureJournalDbContext(DbContextOptions<ErasureJournalDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ErasureJournalEntryEntityConfiguration());
    }
}
