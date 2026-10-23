using Domain.Common.Compliance;
using Infrastructure.AI.Persistence.Configurations;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core DbContext for the document load record (#901): when each loaded document was last loaded. Targets SQLite, in
/// its own database file, matching the repo's one-context-per-subsystem convention.
/// </summary>
[PersonalDataChannel("The document load SQLite database; the document load ledger describes its table.")]
public sealed class DocumentLoadDbContext : DbContext
{
    /// <summary>When each document was last loaded.</summary>
    public DbSet<DocumentLoadEntity> DocumentLoads => Set<DocumentLoadEntity>();

    /// <summary>Initializes a new instance of the <see cref="DocumentLoadDbContext"/> class.</summary>
    /// <param name="options">The context options.</param>
    public DocumentLoadDbContext(DbContextOptions<DocumentLoadDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new DocumentLoadEntityConfiguration());
    }
}
