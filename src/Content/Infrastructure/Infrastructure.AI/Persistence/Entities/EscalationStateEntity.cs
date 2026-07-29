namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core row for one durably persisted escalation in the governance-state store.
/// The full request / decisions / outcome payloads are JSON documents (the domain records
/// are rich, nested, and versioned by shape); the columns EF filters or orders on
/// (status, timestamps) are first-class and indexed.
/// </summary>
/// <remarks>
/// <para>
/// Configured inline in <see cref="Infrastructure.AI.Persistence.GovernanceStateDbContext"/> —
/// deliberately not via an <c>IEntityTypeConfiguration</c>, which
/// <see cref="Infrastructure.AI.Persistence.PlannerDbContext"/> would pick up through its
/// assembly scan. No <c>Version</c> column: rows are single-writer per escalation (the
/// singleton escalation service serializes writes per record), so the planner's
/// <c>SqliteVersionInterceptor</c> concurrency scheme is not applied to this context.
/// </para>
/// <para>
/// Timestamps are stored as raw UTC-tick <see cref="long"/> columns and converted inside the
/// store's guarded per-row mapping, rather than by the <c>SqliteValueConverters</c>
/// <c>DateTimeOffsetAsUtcTicks</c> converter that <c>PromptUsageDbContext</c> and
/// <c>EvalDashboardDbContext</c> apply to their timestamps. A converter's conversion is compiled
/// into EF's query shaper, so it runs during materialization — outside the per-row guard — and
/// <c>new DateTimeOffset(ticks, TimeSpan.Zero)</c> throws for an out-of-range tick. Deferring it
/// keeps one corrupt row quarantinable instead of failing the scan that reads past it.
/// </para>
/// <para>
/// The divergence from those two sibling contexts is deliberate and rests on a difference in
/// requirements, not taste: they serve dashboard queries pulled on demand, where failing the query
/// is an acceptable answer, whereas <see cref="CreatedAtTicks"/> is read by the startup
/// rehydration scan that restores pending human approvals. Losing that whole scan to one bad row
/// would strand every pending approval. Note the guarantee is partial by necessity —
/// <see cref="Id"/> is a Guid BLOB that EF must convert during materialization regardless — which
/// is why <c>EscalationStateRehydrationService</c> additionally treats a scan-level failure as
/// non-fatal (LogCritical and continue to boot) rather than letting it stop the host.
/// </para>
/// </remarks>
public sealed class EscalationStateEntity
{
    /// <summary>The escalation id (primary key).</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The persisted lifecycle status, stored as the
    /// <c>Application.AI.Common.Interfaces.Escalation.EscalationPersistedStatus</c> enum name
    /// ("Pending", "ResolvedPendingAudit", "Resolved") for resilience to enum reordering.
    /// Indexed — the rehydration and reconcile scans filter on it.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>JSON of the originating <c>EscalationRequest</c>.</summary>
    public string RequestJson { get; set; } = string.Empty;

    /// <summary>JSON array of the collected <c>ApproverDecision</c> records, in submission order.</summary>
    public string DecisionsJson { get; set; } = "[]";

    /// <summary>
    /// JSON of the resolved <c>EscalationOutcome</c>; null until a resolution is reached.
    /// </summary>
    public string? OutcomeJson { get; set; }

    /// <summary>
    /// JSON of the <c>EscalationOutcomeSeal</c> covering <see cref="OutcomeJson"/>, produced
    /// when the resolution was recorded. The reconciler refuses to re-drive an outcome whose
    /// seal does not verify against the stored payload, so a row edited outside the process
    /// cannot launder a forged verdict into the compliance audit log. Null on rows written
    /// before sealing existed, which verification treats as unverified (fail-closed).
    /// </summary>
    public string? OutcomeSealJson { get; set; }

    /// <summary>
    /// When the escalation was created by the service, as UTC ticks. Drives timeout
    /// resumption for a rehydrated escalation.
    /// </summary>
    public long CreatedAtTicks { get; set; }

    /// <summary>
    /// When this row was last written, as UTC ticks. Part of the composite status index that
    /// backs the retention prune.
    /// </summary>
    public long UpdatedAtTicks { get; set; }
}
