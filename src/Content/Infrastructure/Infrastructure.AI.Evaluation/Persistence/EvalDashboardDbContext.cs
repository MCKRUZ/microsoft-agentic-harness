using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Evaluation.Persistence;

/// <summary>
/// EF Core DbContext for the eval dashboard durable store (Sub-phase 5.4.1).
/// Targets SQLite. Three tables — runs (header), case results, per-metric scores —
/// support the dashboard's list view, drill-in, and prompt-version comparison
/// without needing to parse JSON blobs at query time.
/// </summary>
public sealed class EvalDashboardDbContext : DbContext
{
    /// <summary>Ingested run headers.</summary>
    public DbSet<EvalRunEntity> EvalRuns => Set<EvalRunEntity>();

    /// <summary>Per-case results within each ingested run.</summary>
    public DbSet<EvalCaseResultEntity> EvalCaseResults => Set<EvalCaseResultEntity>();

    /// <summary>Per-metric aggregated scores within each case result.</summary>
    public DbSet<EvalMetricScoreEntity> EvalMetricScores => Set<EvalMetricScoreEntity>();

    /// <summary>Initializes a new instance.</summary>
    public EvalDashboardDbContext(DbContextOptions<EvalDashboardDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite can't natively ORDER BY DateTimeOffset; round-trip through UTC ticks
        // so range scans and ORDER BY on timestamps work correctly. Shared converter
        // lives in Infrastructure.AI.Persistence so PromptUsage + EvalDashboard stay
        // in sync — any precision/offset fix lands in one place.
        var dateTimeOffsetConverter = SqliteValueConverters.DateTimeOffsetAsUtcTicks;

        var run = modelBuilder.Entity<EvalRunEntity>();
        run.ToTable("eval_runs");
        run.HasKey(e => e.Id);
        run.Property(e => e.Id).ValueGeneratedOnAdd();

        run.Property(e => e.RunId).IsRequired().HasMaxLength(128);
        run.HasIndex(e => e.RunId).IsUnique().HasDatabaseName("ux_eval_runs_run_id");

        run.Property(e => e.StartedAtUtc).HasConversion(dateTimeOffsetConverter);
        run.Property(e => e.CompletedAtUtc).HasConversion(dateTimeOffsetConverter);
        run.Property(e => e.ReceivedAtUtc).HasConversion(dateTimeOffsetConverter);

        run.Property(e => e.DatasetsJson).IsRequired();
        run.Property(e => e.WarningsJson).IsRequired();

        run.HasIndex(e => e.StartedAtUtc).HasDatabaseName("ix_eval_runs_started_at_utc");
        run.HasIndex(e => e.ReceivedAtUtc).HasDatabaseName("ix_eval_runs_received_at_utc");

        var caseResult = modelBuilder.Entity<EvalCaseResultEntity>();
        caseResult.ToTable("eval_case_results");
        caseResult.HasKey(e => e.Id);
        caseResult.Property(e => e.Id).ValueGeneratedOnAdd();

        caseResult.Property(e => e.RunId).IsRequired().HasMaxLength(128);
        caseResult.Property(e => e.DatasetName).IsRequired().HasMaxLength(256);
        caseResult.Property(e => e.CaseId).IsRequired().HasMaxLength(256);
        caseResult.Property(e => e.Input).IsRequired();
        caseResult.Property(e => e.TagsJson).IsRequired();
        caseResult.Property(e => e.InvocationOverridesJson).IsRequired();
        caseResult.Property(e => e.MetricSpecsJson).IsRequired();
        caseResult.Property(e => e.OutputPerRepeatJson).IsRequired();
        caseResult.Property(e => e.ScoresPerRepeatJson).IsRequired();

        // The composite (RunId, CaseId) covers RunId-only queries via prefix scan,
        // so a standalone RunId index would duplicate the leftmost column with no
        // query benefit. Index choice mirrors SQLite's left-prefix scan semantics.
        caseResult.HasIndex(e => new { e.RunId, e.CaseId })
            .HasDatabaseName("ix_eval_case_results_run_id_case_id");

        var metric = modelBuilder.Entity<EvalMetricScoreEntity>();
        metric.ToTable("eval_metric_scores");
        metric.HasKey(e => e.Id);
        metric.Property(e => e.Id).ValueGeneratedOnAdd();

        metric.Property(e => e.RunId).IsRequired().HasMaxLength(128);
        metric.Property(e => e.CaseId).IsRequired().HasMaxLength(256);
        metric.Property(e => e.MetricKey).IsRequired().HasMaxLength(128);

        // (RunId, MetricKey) covers RunId-only scans via prefix; standalone RunId
        // index would duplicate the leftmost column with no query benefit.
        metric.HasIndex(e => new { e.CaseId, e.MetricKey })
            .HasDatabaseName("ix_eval_metric_scores_case_id_metric_key");
        metric.HasIndex(e => new { e.RunId, e.MetricKey })
            .HasDatabaseName("ix_eval_metric_scores_run_id_metric_key");
    }
}
