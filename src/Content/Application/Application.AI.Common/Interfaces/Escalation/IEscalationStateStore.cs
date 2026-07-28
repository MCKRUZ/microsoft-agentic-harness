using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Durable persistence for in-flight escalation state, so pending escalations survive host
/// restarts. Complements — never replaces — the append-only <see cref="IEscalationAuditStore"/>:
/// the audit store is the compliance record; this store is the recoverable working state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure contract:</b> every write is fail-closed at the call site. Implementations
/// signal failure by throwing; the escalation service translates a throw into "the operation
/// was not accepted" (an escalation that cannot be durably created is not created; a decision
/// that cannot be durably recorded is not reported recorded). The no-op
/// <c>NullEscalationStateStore</c> is registered when durable escalation state is disabled,
/// which preserves the in-memory-only behavior exactly.
/// </para>
/// <para>
/// <b>What durability cannot restore:</b> in-process waiters. A caller blocked inside
/// <c>IEscalationService.RequestEscalationAsync</c> holds a
/// <see cref="System.Threading.Tasks.TaskCompletionSource"/> that dies with the process. After a
/// restart the escalation record is rehydrated as pending (decidable, listable, cancellable) and
/// its outcome is durably queryable, but no code is released by the eventual decision — the
/// resumed workflow must poll <c>IEscalationService.GetOutcomeAsync</c>, as the plan executor's
/// resume path already does.
/// </para>
/// <para>
/// Idempotency: <see cref="SavePendingAsync"/> and <see cref="MarkResolvedPendingAuditAsync"/>
/// are upserts keyed by escalation id; repeating a call with the same payload is a no-op-equivalent.
/// This is what makes the reconciler's re-drive safe to run repeatedly.
/// </para>
/// </remarks>
public interface IEscalationStateStore
{
    /// <summary>
    /// Persists a newly created escalation as <see cref="EscalationPersistedStatus.Pending"/>
    /// with an empty decision list. Upserts on the escalation id.
    /// </summary>
    /// <param name="request">The escalation request to persist.</param>
    /// <param name="createdAt">The service-side creation instant, used for timeout resumption.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SavePendingAsync(EscalationRequest request, DateTimeOffset createdAt, CancellationToken ct);

    /// <summary>
    /// Replaces the persisted decision list for a pending escalation with the given snapshot.
    /// Throws when no record exists for <paramref name="escalationId"/> — a decision must never
    /// be durably recorded against an escalation that was never durably created.
    /// </summary>
    /// <param name="escalationId">The escalation whose decisions changed.</param>
    /// <param name="decisions">The full decision list after the change, in submission order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveDecisionsAsync(Guid escalationId, IReadOnlyList<ApproverDecision> decisions, CancellationToken ct);

    /// <summary>
    /// Records that a resolution was reached, moving the record to
    /// <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> with the outcome attached.
    /// Called <i>before</i> the fail-closed audit write so that an audit outage leaves a
    /// detectable stuck state instead of a silently lost verdict. Idempotent upsert on the
    /// escalation id.
    /// </summary>
    /// <param name="outcome">The resolved outcome.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkResolvedPendingAuditAsync(EscalationOutcome outcome, CancellationToken ct);

    /// <summary>
    /// Marks the record terminal (<see cref="EscalationPersistedStatus.Resolved"/>) after the
    /// audit write succeeded. From this point the outcome is served by
    /// <see cref="GetResolvedOutcomeAsync"/> and the record is excluded from
    /// <see cref="GetActiveAsync"/>.
    /// </summary>
    /// <param name="escalationId">The escalation to finalize.</param>
    /// <param name="ct">Cancellation token.</param>
    Task MarkResolvedAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Deletes the record for an escalation that was abandoned before resolution (the blocking
    /// caller cancelled). No-op when the record does not exist.
    /// </summary>
    /// <param name="escalationId">The escalation to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Atomically claims a <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> record
    /// for re-drive, moving it to <see cref="EscalationPersistedStatus.AuditInFlight"/>.
    /// </summary>
    /// <remarks>
    /// This is the concurrency control for reconciliation. Without it, a scheduled pass and an
    /// operator-triggered pass that overlap would each re-drive the same record: a duplicate
    /// compliance audit line is tolerable, but a duplicate resolution notification — which
    /// fans out to the drift/AG-UI bridge — is not. Implementations must make the
    /// read-and-transition a single conditional statement, not a read followed by a write.
    /// </remarks>
    /// <param name="escalationId">The escalation to claim.</param>
    /// <param name="staleClaimBefore">
    /// Cutoff for reclaiming an abandoned claim. A record already in
    /// <see cref="EscalationPersistedStatus.AuditInFlight"/> whose last update predates this
    /// instant is treated as orphaned and may be claimed. Without this, a pass killed between
    /// claiming and finishing (kill -9, OOM, pod eviction) never releases its claim, and the
    /// record becomes permanently unclaimable — invisible to every later pass and untouchable
    /// by the pruner, which correctly refuses to delete non-terminal rows. The bound is what
    /// prevents a merely-slow live pass from having its claim stolen.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when this caller now owns the re-drive; false when someone else does.</returns>
    Task<bool> TryClaimResolvedPendingAuditAsync(
        Guid escalationId, DateTimeOffset staleClaimBefore, CancellationToken ct);

    /// <summary>
    /// Returns a claimed record to <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/>
    /// after a failed re-drive, so a future pass can retry it. No-op when the record is not
    /// currently claimed.
    /// </summary>
    /// <param name="escalationId">The escalation whose claim to release.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseClaimAsync(Guid escalationId, CancellationToken ct);

    /// <summary>
    /// Returns all non-terminal records: <see cref="EscalationPersistedStatus.Pending"/> ones
    /// (rehydrated into the active set on startup) and
    /// <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> ones (finalized by the
    /// reconciler). Implementations skip and log records that fail to deserialize rather than
    /// failing the whole read — one poisoned row must not block startup recovery of the rest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Snapshots of every non-terminal escalation, unordered.</returns>
    Task<IReadOnlyList<EscalationStateSnapshot>> GetActiveAsync(CancellationToken ct);

    /// <summary>
    /// Returns the durably audited outcome for an escalation, or null when the escalation is
    /// unknown, still pending, or resolved-but-not-yet-audited. Deliberately excludes
    /// <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> records: an outcome whose
    /// audit write has not completed must never be observable (fail-closed).
    /// </summary>
    /// <param name="escalationId">The escalation to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The audited outcome, or null.</returns>
    Task<EscalationOutcome?> GetResolvedOutcomeAsync(Guid escalationId, CancellationToken ct);
}
