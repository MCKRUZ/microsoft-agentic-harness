using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Escalation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Durability and recovery surface of <see cref="DefaultEscalationService"/>: startup
/// rehydration of pending escalations from the <see cref="IEscalationStateStore"/>, and the
/// <see cref="IEscalationReconciler"/> implementation that re-drives resolutions stuck on a
/// failed durable or audit write.
/// </summary>
public sealed partial class DefaultEscalationService
{
	/// <summary>
	/// How long a reconcile claim may sit in <see cref="EscalationPersistedStatus.AuditInFlight"/>
	/// before another pass may reclaim it as orphaned.
	/// </summary>
	/// <remarks>
	/// A pass that is killed between claiming and finishing never runs its release, so without
	/// an expiry the record would be permanently unclaimable: skipped by every future pass and
	/// left alone by the pruner, which correctly refuses to delete non-terminal rows. Ten
	/// minutes is far longer than a healthy re-drive (a JSONL append plus one row update) yet
	/// short enough that a stranded approval is recovered on the next scheduled pass.
	/// </remarks>
	private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Tolerance for a rehydrated record whose <c>CreatedAt</c> is in the future — small enough
	/// to reject a corrupt or forged far-future timestamp, large enough to absorb ordinary clock
	/// skew between the writer and this host.
	/// </summary>
	private static readonly TimeSpan CreatedAtFutureSkew = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Loads every <see cref="EscalationPersistedStatus.Pending"/> record from the durable
	/// store into the active in-memory set, making it decidable, listable, and cancellable
	/// again, and resumes each escalation's ORIGINAL timeout (downtime counts against the
	/// budget; an already-expired escalation times out immediately, with a
	/// <c>TimeoutAction.Approve</c> downgraded to a denial — see <c>ResolveTimeoutApproval</c>).
	/// </summary>
	/// <remarks>
	/// <para>
	/// Invoked once at host startup by <see cref="EscalationStateRehydrationService"/>. With
	/// the Null store (durability disabled) this is a no-op returning zero. Blocking waiters
	/// are NOT restored — they cannot be (see the class remarks); rehydrated escalations
	/// release nobody when resolved, but their outcomes are durably queryable via
	/// <see cref="IEscalationService.GetOutcomeAsync"/>.
	/// </para>
	/// <para>
	/// <b>Every row is revalidated.</b> The store is a file, not a value this process just
	/// constructed, so each snapshot passes <see cref="EscalationRequestInvariants"/> before it
	/// may become live — the same gate <c>InitializeEscalation</c> applies. A row violating an
	/// invariant is skipped and logged rather than resurrected: an empty roster with
	/// <c>TimeoutAction.Approve</c>, or a zero Quorum threshold, would otherwise rehydrate as a
	/// live escalation nobody can vote on that auto-approves itself on timeout.
	/// </para>
	/// <para>
	/// Records in <see cref="EscalationPersistedStatus.ResolvedPendingAudit"/> or
	/// <see cref="EscalationPersistedStatus.AuditInFlight"/> are deliberately NOT rehydrated as
	/// pending — their resolution already happened; they are finalized by
	/// <see cref="ReconcileStuckEscalationsAsync"/>. Rehydration re-sends no notifications and
	/// re-writes no audit records: both already happened in the previous process lifetime.
	/// </para>
	/// <para>
	/// Individual unreadable or invalid rows are skipped and logged rather than aborting the
	/// scan. A scan-level failure propagates to <see cref="EscalationStateRehydrationService"/>,
	/// which logs it at <c>Critical</c> and lets the host start anyway — see that type's remarks
	/// for why one corrupt byte must not become a full-availability outage.
	/// </para>
	/// </remarks>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The number of escalations restored to the active set.</returns>
	public async Task<int> RehydratePendingEscalationsAsync(CancellationToken ct)
	{
		var snapshots = await _stateStore.GetActiveAsync(ct);
		var restored = 0;

		foreach (var snapshot in snapshots)
		{
			if (snapshot.Status != EscalationPersistedStatus.Pending)
				continue;

			if (!EscalationRequestInvariants.TryValidate(snapshot.Request, out var violation) ||
				!TryValidateCreatedAt(snapshot, out violation))
			{
				_logger.LogError(
					"Refusing to rehydrate durable escalation {EscalationId}: {Violation}. " +
					"The row is preserved for investigation and will not become decidable",
					snapshot.Request.EscalationId, violation);
				continue;
			}

			if (TryRestore(snapshot))
				restored++;
		}

		return restored;
	}

	/// <summary>
	/// Validates the persisted <c>CreatedAt</c>, which reaches this process as a raw tick value
	/// and is therefore no more trustworthy than the request payload.
	/// </summary>
	/// <remarks>
	/// <see cref="EscalationRequestInvariants"/> can only cover fields on the request, so the
	/// timeout ceiling it enforces is reachable around: a far-future <c>CreatedAt</c> that is
	/// still a valid <see cref="DateTimeOffset"/> makes <c>CreatedAt + TimeoutSeconds</c>
	/// overflow, the timeout loop's catch-all swallows the throw, and the escalation is pending
	/// forever — exactly the immortal-escalation harm the ceiling exists to prevent, reached
	/// through a different field. Both halves are checked here: the instant itself must not be
	/// meaningfully in the future, and the resulting deadline must be representable.
	/// </remarks>
	/// <param name="snapshot">The snapshot being considered for rehydration.</param>
	/// <param name="violation">On failure, an operator-readable description; null on success.</param>
	/// <returns>True when the timestamp is usable.</returns>
	private static bool TryValidateCreatedAt(EscalationStateSnapshot snapshot, out string? violation)
	{
		var now = DateTimeOffset.UtcNow;
		if (snapshot.CreatedAt > now + CreatedAtFutureSkew)
		{
			violation =
				$"the persisted creation time ({snapshot.CreatedAt:O}) is in the future beyond the " +
				$"{CreatedAtFutureSkew.TotalMinutes:0}-minute skew allowance";
			return false;
		}

		try
		{
			_ = snapshot.CreatedAt + TimeSpan.FromSeconds(snapshot.Request.TimeoutSeconds);
		}
		catch (ArgumentOutOfRangeException)
		{
			violation =
				$"the resumed deadline (created {snapshot.CreatedAt:O} plus " +
				$"{snapshot.Request.TimeoutSeconds}s) is not representable";
			return false;
		}

		violation = null;
		return true;
	}

	/// <summary>
	/// Adds one validated snapshot to the active set and starts its resumed timeout.
	/// </summary>
	/// <param name="snapshot">A snapshot that already passed invariant validation.</param>
	/// <returns>True when the escalation was added; false when one with that id was already active.</returns>
	private bool TryRestore(EscalationStateSnapshot snapshot)
	{
		// Decide expiry ONCE, here, rather than re-testing the deadline after Task.Delay
		// returns. Timer granularity (~15ms on Windows) or a backwards clock step can leave the
		// deadline marginally in the future at timeout time, which would skip the fail-closed
		// downgrade and auto-approve an escalation nobody was re-notified about.
		var deadline = snapshot.CreatedAt + TimeSpan.FromSeconds(snapshot.Request.TimeoutSeconds);
		var state = new EscalationState
		{
			Request = snapshot.Request,
			TimeoutCts = new CancellationTokenSource(),
			CreatedAt = snapshot.CreatedAt,
			WasRehydrated = true,
			ExpiredDuringDowntime = deadline <= DateTimeOffset.UtcNow
		};
		state.Decisions.AddRange(snapshot.Decisions);

		if (!_activeEscalations.TryAdd(snapshot.Request.EscalationId, state))
		{
			state.TimeoutCts.Dispose();
			state.WriteGate.Dispose();
			return false;
		}

		EscalationMetrics.Pending.Add(1);
		_ = RunTimeoutAsync(state);

		_logger.LogInformation(
			"Rehydrated pending escalation {EscalationId} for agent {AgentId} ({DecisionCount} decision(s) restored)",
			snapshot.Request.EscalationId, snapshot.Request.AgentId, snapshot.Decisions.Count);
		return true;
	}

	/// <inheritdoc />
	public async Task<EscalationReconcileResult> ReconcileStuckEscalationsAsync(CancellationToken ct)
	{
		// One pass at a time within the process. Combined with the store's conditional durable
		// claim, this keeps the compliance audit line and the resolution notification firing
		// once per stuck escalation even when a timer tick and an operator-triggered pass
		// overlap.
		await _reconcileGate.WaitAsync(ct);
		try
		{
			var recovered = new List<Guid>();
			var stillStuck = new List<Guid>();

			await ReconcileInMemoryAsync(recovered, stillStuck, ct);
			await ReconcileDurableAsync(recovered, stillStuck, ct);

			// Information only when the pass actually did something. A host with the feature off
			// still runs this pass on every tick, and an unconditional Information line would ship
			// hundreds of "0 recovered, 0 stuck" records per host per day through the collector for
			// no operational value. Idle passes stay observable at Debug.
			if (recovered.Count > 0 || stillStuck.Count > 0)
			{
				_logger.LogInformation(
					"Escalation reconcile pass complete: {RecoveredCount} recovered, {StuckCount} still stuck",
					recovered.Count, stillStuck.Count);
			}
			else
			{
				_logger.LogDebug("Escalation reconcile pass complete: nothing to recover");
			}

			return new EscalationReconcileResult
			{
				Recovered = recovered,
				StillStuck = stillStuck
			};
		}
		finally
		{
			_reconcileGate.Release();
		}
	}

	/// <summary>
	/// Shape 1 — in-memory stuck states: a resolution was reached this process lifetime but its
	/// fail-closed durable or audit write faulted, leaving the escalation in the active set with
	/// <c>IsResolved</c>, a stashed outcome, and a released finalize claim.
	/// </summary>
	/// <param name="recovered">Accumulates escalations finalized by this pass.</param>
	/// <param name="stillStuck">Accumulates escalations that remain stuck.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task ReconcileInMemoryAsync(List<Guid> recovered, List<Guid> stillStuck, CancellationToken ct)
	{
		foreach (var state in _activeEscalations.Values)
		{
			EscalationOutcome? pending;
			lock (state.Lock)
			{
				pending = state.IsResolved && !state.FinalizeClaimed ? state.PendingOutcome : null;
				if (pending is not null)
					state.FinalizeClaimed = true;
			}

			if (pending is null)
				continue;

			if (await TryFinalizeStuckStateAsync(state, pending, ct))
			{
				recovered.Add(pending.EscalationId);
			}
			else
			{
				lock (state.Lock)
				{
					state.FinalizeClaimed = false;
				}
				stillStuck.Add(pending.EscalationId);
			}
		}
	}

	/// <summary>
	/// Shape 2 — durable-only stuck records: rows parked by a previous process lifetime (the
	/// crash landed between the resolution and the audit write, or between the audit write and
	/// the terminal marker). No in-memory state exists for them.
	/// </summary>
	/// <remarks>
	/// Each record is claimed through the store's conditional update before any side effect
	/// runs, so overlapping passes cannot both append the compliance line and both fire the
	/// resolution notification. A snapshot whose outcome failed seal verification arrives with
	/// a null outcome and is skipped — a tampered verdict is never re-driven into the audit log.
	/// </remarks>
	/// <param name="recovered">Accumulates escalations finalized by this pass.</param>
	/// <param name="stillStuck">Accumulates escalations that remain stuck.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task ReconcileDurableAsync(List<Guid> recovered, List<Guid> stillStuck, CancellationToken ct)
	{
		var snapshots = await _stateStore.GetActiveAsync(ct);
		foreach (var snapshot in snapshots)
		{
			var escalationId = snapshot.Request.EscalationId;
			if (!IsDurableStuckCandidate(snapshot) ||
				recovered.Contains(escalationId) ||
				stillStuck.Contains(escalationId) ||
				_activeEscalations.ContainsKey(escalationId))
			{
				continue;
			}

			var staleClaimBefore = DateTimeOffset.UtcNow - StaleClaimAge;
			if (!await _stateStore.TryClaimResolvedPendingAuditAsync(escalationId, staleClaimBefore, ct))
			{
				// An unclaimable ResolvedPendingAudit row means a genuine concurrent pass owns
				// it — routine. An unclaimable AuditInFlight row means another pass is mid-drive
				// and its claim has not yet aged out; that is worth surfacing louder, because a
				// row still being skipped after the staleness window would indicate a claim that
				// is neither progressing nor expiring.
				if (snapshot.Status == EscalationPersistedStatus.AuditInFlight)
				{
					_logger.LogWarning(
						"Escalation {EscalationId} is held in AuditInFlight by another reconcile pass and is not yet " +
						"reclaimable (claims age out after {StaleClaimAge}); skipping this pass",
						escalationId, StaleClaimAge);
				}
				else
				{
					_logger.LogDebug(
						"Escalation {EscalationId} is already claimed by another reconcile pass; skipping",
						escalationId);
				}
				continue;
			}

			if (await TryFinalizeDurableRecordAsync(snapshot.Outcome!, ct))
			{
				recovered.Add(escalationId);
			}
			else
			{
				await SafeExecuteAsync(
					() => _stateStore.ReleaseClaimAsync(escalationId, CancellationToken.None),
					"release reconcile claim", escalationId);
				stillStuck.Add(escalationId);
			}
		}
	}

	/// <summary>
	/// True when a snapshot is a durable-only stuck record with a verified outcome available to
	/// re-drive. An <see cref="EscalationPersistedStatus.AuditInFlight"/> row is included: it
	/// belongs to a pass that crashed mid-claim, and leaving it would strand the escalation.
	/// </summary>
	/// <param name="snapshot">The snapshot to classify.</param>
	private static bool IsDurableStuckCandidate(EscalationStateSnapshot snapshot) =>
		snapshot.Outcome is not null &&
		snapshot.Status is EscalationPersistedStatus.ResolvedPendingAudit
			or EscalationPersistedStatus.AuditInFlight;

	/// <summary>
	/// Re-drives the durable marker and audit write for an in-memory stuck state, then
	/// finalizes it. Returns false (leaving the state stuck and claimable again) when the audit
	/// or durable store is still failing.
	/// </summary>
	/// <param name="state">The stuck escalation state (resolution reached, never finalized).</param>
	/// <param name="outcome">The stashed resolution to re-drive.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task<bool> TryFinalizeStuckStateAsync(
		EscalationState state, EscalationOutcome outcome, CancellationToken ct)
	{
		try
		{
			// Same fail-closed order as ResolveEscalationAsync: durable marker, then audit.
			// Both are idempotent, so re-driving after a partial earlier attempt is safe (at
			// worst a duplicate same-content audit line).
			await _stateStore.MarkResolvedPendingAuditAsync(outcome, ct);
			await _auditStore.RecordOutcomeAsync(outcome, ct);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex,
				"Reconcile could not finalize escalation {EscalationId}; it remains stuck for a future pass",
				outcome.EscalationId);
			return false;
		}

		// FinalizeResolvedAsync marks the durable record terminal, publishes the verdict,
		// removes the escalation from the active set, notifies, and completes the waiter. The
		// Completion task already faulted when the original write failed, so its TrySetResult
		// is a deliberate no-op — the blocked caller was already released with the failure,
		// fail-closed.
		await FinalizeResolvedAsync(state, outcome);

		_logger.LogInformation(
			"Reconciled stuck escalation {EscalationId}: {ResolutionType}, approved={IsApproved}",
			outcome.EscalationId, outcome.ResolutionType, outcome.IsApproved);
		return true;
	}

	/// <summary>
	/// Finalizes a durable-only stuck record left behind by a previous process lifetime:
	/// re-drives the audit write, marks the record terminal, publishes the outcome for pollers,
	/// and notifies. Returns false when the audit or durable store is still failing.
	/// </summary>
	/// <param name="outcome">The persisted, seal-verified resolution to re-drive.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task<bool> TryFinalizeDurableRecordAsync(EscalationOutcome outcome, CancellationToken ct)
	{
		try
		{
			await _auditStore.RecordOutcomeAsync(outcome, ct);
			await _stateStore.MarkResolvedAsync(outcome.EscalationId, ct);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex,
				"Reconcile could not finalize durable escalation record {EscalationId}; it remains stuck for a future pass",
				outcome.EscalationId);
			return false;
		}

		_resolvedOutcomes[outcome.EscalationId] = outcome;

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationResolvedAsync(outcome, CancellationToken.None),
			"notify reconciled resolution", outcome.EscalationId);

		_logger.LogInformation(
			"Reconciled durable stuck escalation {EscalationId} from a previous process lifetime: {ResolutionType}, approved={IsApproved}",
			outcome.EscalationId, outcome.ResolutionType, outcome.IsApproved);
		return true;
	}
}
