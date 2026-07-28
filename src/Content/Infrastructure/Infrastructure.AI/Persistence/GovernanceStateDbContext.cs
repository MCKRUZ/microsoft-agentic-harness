using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core DbContext for the durable governance-state store: pending escalations and change
/// proposals that must survive host restarts. Targets SQLite; timestamps are stored as raw
/// UTC-tick <see cref="long"/> columns (so range scans and ordering work, and a corrupt value
/// surfaces in the store's guarded per-row mapping rather than throwing during
/// materialization), and enums are stored as strings for resilience to reordering.
/// </summary>
/// <remarks>
/// <para>
/// One context for both governance subsystems by design: escalations and change proposals are
/// the two approval-workflow states the harness tracks, they ship behind the same
/// <c>AppConfig:AI:Governance:DurableState</c> section, and a single database file keeps the
/// operator surface (one file to back up, inspect, or wipe) simple. Kept separate from
/// <see cref="PlannerDbContext"/> (plan execution state) and <c>PromptUsageDbContext</c>
/// (telemetry history), matching the repo's one-context-per-subsystem convention.
/// </para>
/// <para>
/// Entities are configured inline rather than via <c>IEntityTypeConfiguration</c> classes
/// because <see cref="PlannerDbContext"/> applies every configuration in this assembly —
/// inline configuration keeps the two models from bleeding into each other.
/// </para>
/// <para>
/// Every index here backs a real query: the composite <c>(Status, UpdatedAtTicks)</c> serves
/// both the rehydration/reconcile status scans and the retention prune's status-plus-age
/// range; the proposal indexes mirror each filter dimension of
/// <c>IChangeProposalStore.ListAsync</c>, including the <c>BlastRadius</c> range predicate.
/// </para>
/// </remarks>
public sealed class GovernanceStateDbContext : DbContext
{
    /// <summary>Durably persisted escalation state records.</summary>
    public DbSet<EscalationStateEntity> Escalations => Set<EscalationStateEntity>();

    /// <summary>Durably persisted change proposals.</summary>
    public DbSet<ChangeProposalEntity> ChangeProposals => Set<ChangeProposalEntity>();

    /// <summary>Initializes a new instance.</summary>
    /// <param name="options">The context options (connection string, provider).</param>
    public GovernanceStateDbContext(DbContextOptions<GovernanceStateDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var escalation = modelBuilder.Entity<EscalationStateEntity>();
        escalation.ToTable("escalation_state");
        escalation.HasKey(e => e.Id);
        escalation.Property(e => e.Status).IsRequired().HasMaxLength(32);
        escalation.Property(e => e.RequestJson).IsRequired();
        escalation.Property(e => e.DecisionsJson).IsRequired();
        escalation.HasIndex(e => new { e.Status, e.UpdatedAtTicks })
            .HasDatabaseName("ix_escalation_state_status_updated_at");

        var proposal = modelBuilder.Entity<ChangeProposalEntity>();
        proposal.ToTable("change_proposal");
        proposal.HasKey(p => p.Id);
        proposal.Property(p => p.Id).HasMaxLength(128);
        proposal.Property(p => p.Status).IsRequired().HasMaxLength(32);
        proposal.Property(p => p.SubmittedByAgentId).IsRequired().HasMaxLength(256);
        proposal.Property(p => p.TargetKind).IsRequired().HasMaxLength(64);
        proposal.Property(p => p.ProposalJson).IsRequired();
        proposal.HasIndex(p => new { p.Status, p.SubmittedAtTicks })
            .HasDatabaseName("ix_change_proposal_status_submitted_at");
        proposal.HasIndex(p => p.SubmittedAtTicks)
            .HasDatabaseName("ix_change_proposal_submitted_at");
        proposal.HasIndex(p => p.SubmittedByAgentId)
            .HasDatabaseName("ix_change_proposal_submitted_by");
        proposal.HasIndex(p => p.BlastRadius)
            .HasDatabaseName("ix_change_proposal_blast_radius");
    }
}
