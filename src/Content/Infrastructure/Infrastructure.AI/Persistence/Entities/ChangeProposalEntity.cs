namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core row for one durably persisted <c>ChangeProposal</c> in the governance-state store.
/// The aggregate itself is stored as a JSON document (its diff, history, and polymorphic
/// target don't map usefully to columns); the dimensions
/// <c>IChangeProposalStore.ListAsync</c> filters or orders on are denormalized into
/// first-class, indexed columns and kept in sync on every save.
/// </summary>
/// <remarks>
/// <para>
/// Configured inline in <see cref="Infrastructure.AI.Persistence.GovernanceStateDbContext"/> —
/// deliberately not via an <c>IEntityTypeConfiguration</c>, which
/// <see cref="Infrastructure.AI.Persistence.PlannerDbContext"/> would pick up through its
/// assembly scan. No <c>Version</c> column: <c>SaveAsync</c> is contractually last-write-wins
/// on the proposal id, so the planner's <c>SqliteVersionInterceptor</c> scheme is not applied.
/// </para>
/// <para>
/// <see cref="SubmittedAtTicks"/> is a raw UTC-tick <see cref="long"/> for the same reason as
/// the escalation entity's timestamps: an EF value converter throws during materialization,
/// outside the store's per-row guard, so one corrupt value would fail an entire list query.
/// </para>
/// </remarks>
public sealed class ChangeProposalEntity
{
    /// <summary>The proposal's deterministic id (Base64URL SHA-256; primary key).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The lifecycle status, stored as the <c>ChangeProposalStatus</c> enum name for
    /// resilience to enum reordering. Indexed — the primary list filter.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The submitting agent's id (<c>ChangeProposal.SubmittedBy.Id</c>). Indexed.</summary>
    public string SubmittedByAgentId { get; set; } = string.Empty;

    /// <summary>
    /// The proposal's blast radius as the underlying enum integer, so the
    /// "at or above" list filter translates to an indexed SQL range predicate.
    /// </summary>
    public int BlastRadius { get; set; }

    /// <summary>The target kind, stored as the <c>ChangeTargetKind</c> enum name.</summary>
    public string TargetKind { get; set; } = string.Empty;

    /// <summary>
    /// The submission instant as UTC ticks. Indexed — lists order by it, most recent first,
    /// and the retention prune ranges over it.
    /// </summary>
    public long SubmittedAtTicks { get; set; }

    /// <summary>JSON of the full <c>ChangeProposal</c> aggregate (source of truth on read).</summary>
    public string ProposalJson { get; set; } = string.Empty;

    /// <summary>
    /// JSON of the <c>GovernanceRecordSeal</c> covering <see cref="ProposalJson"/>, bound to
    /// <see cref="Id"/>. Without it a database writer could move a proposal to an approved or
    /// terminal <see cref="Status"/>, or widen its target scope, and reads would serve the
    /// result unchallenged — an exposure the in-memory store did not have, because there was no
    /// file to edit. Null on rows written before sealing existed; verification treats that as
    /// unverified (fail-closed).
    /// </summary>
    public string? ProposalSealJson { get; set; }
}
