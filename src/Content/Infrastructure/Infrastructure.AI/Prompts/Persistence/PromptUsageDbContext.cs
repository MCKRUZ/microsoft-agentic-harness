using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Prompts.Persistence;

/// <summary>
/// EF Core DbContext for the prompt-usage durable store. Targets SQLite with
/// append-only writes and read-side indexes on (TraceId, CaseId, RecordedAtUtc)
/// so trace-replay queries can efficiently recover historical prompt assignments.
/// </summary>
public sealed class PromptUsageDbContext : DbContext
{
    /// <summary>Append-only prompt usage events.</summary>
    public DbSet<PromptUsageEntity> PromptUsages => Set<PromptUsageEntity>();

    /// <summary>Initializes a new instance.</summary>
    public PromptUsageDbContext(DbContextOptions<PromptUsageDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PromptUsageEntity>();
        entity.ToTable("prompt_usage");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedOnAdd();

        entity.Property(e => e.PromptName).IsRequired().HasMaxLength(256);
        entity.Property(e => e.PromptHash).IsRequired().HasMaxLength(64);

        // SQLite can't natively ORDER BY DateTimeOffset; round-trip through UTC ticks
        // so range scans and ORDER BY on RecordedAtUtc work correctly. Shared converter
        // lives in Infrastructure.AI.Persistence so PromptUsage + EvalDashboard stay in sync.
        entity.Property(e => e.RecordedAtUtc)
            .HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        entity.HasIndex(e => e.TraceId).HasDatabaseName("ix_prompt_usage_trace_id");
        entity.HasIndex(e => e.CaseId).HasDatabaseName("ix_prompt_usage_case_id");
        entity.HasIndex(e => e.RecordedAtUtc).HasDatabaseName("ix_prompt_usage_recorded_at_utc");
        entity.HasIndex(e => new { e.PromptName, e.PromptVersionMajor, e.PromptVersionMinor })
            .HasDatabaseName("ix_prompt_usage_name_version");
    }
}
