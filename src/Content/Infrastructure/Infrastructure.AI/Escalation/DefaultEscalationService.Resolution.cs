using Application.AI.Common.Exceptions;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Resolution, timeout, and teardown half of <see cref="DefaultEscalationService"/>: the
/// fail-closed write ordering that decides whether an escalation may be reported resolved.
/// </summary>
public sealed partial class DefaultEscalationService
{
	/// <summary>
	/// Marks a state as resolving under the caller's <c>state.Lock</c>: flips
	/// <c>IsResolved</c>, stashes the outcome for the reconciler, and takes the finalize claim
	/// so no concurrent reconcile pass can finalize a resolution this path still owns.
	/// </summary>
	/// <param name="state">The escalation being resolved. Caller must hold its lock.</param>
	/// <param name="outcome">The resolution reached.</param>
	private static void MarkResolving(EscalationState state, EscalationOutcome outcome)
	{
		state.IsResolved = true;
		state.PendingOutcome = outcome;
		state.FinalizeClaimed = true;
		state.ResolutionFailed = false;
	}

	/// <summary>
	/// Releases the finalize claim after a fail-closed write failed, so the escalation becomes
	/// claimable by a reconcile pass, and records that it is parked awaiting reconciliation.
	/// </summary>
	/// <param name="state">The escalation whose resolution failed.</param>
	private static void MarkResolutionFailed(EscalationState state)
	{
		lock (state.Lock)
		{
			state.FinalizeClaimed = false;
			state.ResolutionFailed = true;
		}
	}

	private async Task ResolveEscalationAsync(EscalationState state, EscalationOutcome outcome)
	{
		// Idempotency / teardown guard: if the completion was already settled
		// (e.g. the service was disposed and cancelled it), don't re-run cleanup.
		// Each caller (SubmitDecision/Cancel/Timeout) marks the state resolving under
		// the state lock before calling here, so this runs at most once per escalation.
		if (state.Completion.Task.IsCompleted)
			return;

		state.TimeoutCts.Cancel();

		// Durable resolution marker BEFORE the fail-closed audit write below: if the audit
		// store is down, the durable record parks in ResolvedPendingAudit — the detectable
		// state the IEscalationReconciler re-drives once the audit store recovers — instead of
		// the verdict existing only in process memory. Fail-closed with the same semantics as
		// the audit write: a resolution that cannot be durably recorded is not reported
		// resolved, and the escalation stays observable in the active set.
		try
		{
			await _stateStore.MarkResolvedPendingAuditAsync(outcome, CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex,
				"Failed to durably record resolution for escalation {EscalationId}; failing closed (escalation not reported resolved)",
				outcome.EscalationId);
			MarkResolutionFailed(state);
			var scrubbed = new EscalationDurableStateException(
				EscalationDurableStateException.DurableResolutionFailedCode, ex);
			state.Completion.TrySetException(scrubbed);
			throw scrubbed;
		}

		// Record the audit outcome BEFORE releasing the caller awaiting Completion.Task.
		// The durable outcome write is fail-CLOSED: if it throws, the escalation must NOT
		// be reported as resolved. Propagate the failure to the awaiting caller instead of
		// delivering an approval that was never recorded for compliance. (SafeExecuteAsync
		// is reserved for best-effort notification, never for the durable audit write.)
		// The escalation deliberately stays in _activeEscalations on failure: it must remain
		// observable (a resolved-but-unaudited escalation vanishing entirely would read as an
		// unknown id to every poller) rather than dropping into a state no reader can see.
		try
		{
			await _auditStore.RecordOutcomeAsync(outcome, CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex,
				"Failed to record outcome for escalation {EscalationId}; failing closed (escalation not reported resolved)",
				outcome.EscalationId);
			MarkResolutionFailed(state);
			state.Completion.TrySetException(ex);
			throw;
		}

		await FinalizeResolvedAsync(state, outcome);
	}

	/// <summary>
	/// Completes a resolution whose durable and audit writes both succeeded: marks the durable
	/// record terminal, publishes the verdict, removes the escalation from the active set,
	/// records metrics, notifies, and releases the blocked caller.
	/// </summary>
	/// <param name="state">The resolved escalation.</param>
	/// <param name="outcome">The audited outcome.</param>
	private async Task FinalizeResolvedAsync(EscalationState state, EscalationOutcome outcome)
	{
		// Terminal durable marker, deliberately best-effort: the verdict is durably audited at
		// this point, so faulting the caller now would be dishonest. On failure the row stays
		// in ResolvedPendingAudit and a later reconcile pass finalizes it — possibly appending
		// a duplicate (same-content) outcome audit line, the documented safe trade against
		// losing a verdict. No-op with the Null store.
		try
		{
			await _stateStore.MarkResolvedAsync(outcome.EscalationId, CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex,
				"Failed to finalize durable state for escalation {EscalationId}; outcome is audited, reconcile will finalize the record",
				outcome.EscalationId);
		}

		// The parked-awaiting-reconciliation marker is now false in fact, so clear it before the
		// verdict becomes observable. Without this, a decision arriving in the window between the
		// successful audit write and the removal from the active set below would be told the
		// escalation is awaiting reconciliation (409) when it has actually just resolved.
		lock (state.Lock)
		{
			state.ResolutionFailed = false;
		}

		// Retain the verdict ONLY after it has been durably audited, so GetOutcomeAsync — and thus
		// the plan executor's resume reconciliation — can never act on a verdict that failed the
		// fail-closed audit write above and was rolled back to the awaiting caller. Publish it
		// BEFORE removing the escalation from the active set: a reader landing between the two
		// steps sees at least one view (brief dual presence is fine — readers check the active
		// set first and simply serve the pending view), never the spurious not-found a
		// remove-then-publish order produced on exactly the poll the 202 contract prescribes.
		_resolvedOutcomes[outcome.EscalationId] = outcome;
		_activeEscalations.TryRemove(state.Request.EscalationId, out _);

		EscalationMetrics.Pending.Add(-1);
		RecordResolutionMetrics(state, outcome);

		_logger.LogInformation(
			"Escalation {EscalationId} resolved: {ResolutionType}, approved={IsApproved}",
			outcome.EscalationId, outcome.ResolutionType, outcome.IsApproved);

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationResolvedAsync(outcome, CancellationToken.None),
			"notify resolution", outcome.EscalationId);

		state.Completion.TrySetResult(outcome);
	}

	private async Task RunTimeoutAsync(EscalationState state)
	{
		try
		{
			// The deadline is anchored to CreatedAt rather than "now" so a rehydrated
			// escalation resumes its ORIGINAL timeout budget — downtime counts against it
			// instead of resetting it. For freshly created escalations CreatedAt is "now"
			// and this is the original full delay. An already-expired deadline times out
			// immediately. TimeoutSeconds is bounded by EscalationRequestInvariants, so the
			// delay can never overflow and silently make an escalation immortal.
			var deadline = state.CreatedAt + TimeSpan.FromSeconds(state.Request.TimeoutSeconds);
			var delay = deadline - DateTimeOffset.UtcNow;
			if (delay > TimeSpan.Zero)
				await Task.Delay(delay, state.TimeoutCts.Token);

			await HandleTimeoutAsync(state);
		}
		catch (OperationCanceledException)
		{
			// Escalation resolved or caller cancelled before timeout -- normal path
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error in timeout handler for escalation {EscalationId}",
				state.Request.EscalationId);
		}
	}

	private async Task HandleTimeoutAsync(EscalationState state)
	{
		// Takes the same per-escalation gate the decision path uses. Without it a decision's
		// SaveDecisionsAsync could land AFTER MarkResolvedPendingAuditAsync has sealed the
		// outcome, leaving DecisionsJson describing a decision set the sealed verdict never saw.
		//
		// A disposed gate means the escalation was torn down (service disposal, caller
		// cancellation) while this timeout task was still in flight. Timing it out is moot at
		// that point, so bail out quietly rather than faulting a background task.
		if (!await TryEnterWriteGateAsync(state, CancellationToken.None))
			return;

		try
		{
			EscalationOutcome? outcome;

			lock (state.Lock)
			{
				if (state.IsResolved)
					return;

				var (resolutionType, isApproved, escalatedToTier) = ResolveTimeoutOutcome(state);

				outcome = new EscalationOutcome
				{
					EscalationId = state.Request.EscalationId,
					IsApproved = isApproved,
					Decisions = state.Decisions.ToList().AsReadOnly(),
					ResolutionType = resolutionType,
					ResolvedAt = DateTimeOffset.UtcNow,
					Approvers = state.Request.Approvers,
					EscalatedToTier = escalatedToTier
				};
				MarkResolving(state, outcome);
			}

			EscalationMetrics.Timeouts.Add(1,
				new KeyValuePair<string, object?>(EscalationConventions.Priority,
					ToPriorityTag(state.Request.Priority)));

			_logger.LogWarning(
				"Escalation {EscalationId} timed out with action {TimeoutAction}",
				state.Request.EscalationId, state.Request.TimeoutAction);

			await ResolveEscalationAsync(state, outcome);
		}
		finally
		{
			// Same teardown race as the acquire: the gate can be disposed underneath a
			// long-running resolution when the host shuts down mid-flight.
			ReleaseWriteGate(state);
		}
	}

	/// <summary>
	/// Decides what a timeout produces: a real tier hand-off (#394) when the request opted in via
	/// <see cref="EscalationRequest.EscalationTierTarget"/> and the configured action calls for
	/// escalation, otherwise the ordinary approve/deny verdict from <see cref="ResolveTimeoutApproval"/>.
	/// </summary>
	/// <remarks>
	/// The escalated branch never auto-grants anything — <c>IsApproved</c> stays false. It only
	/// records that this request should be handed to a higher-authority roster; a caller-owned
	/// downstream process (only <c>CapabilityMatchSupervisor</c>'s delegation-autonomy escalation
	/// today) is what actually raises the follow-up request and decides whether to act on approval.
	/// This keeps the branch fail-closed under the exact same rehydration-during-downtime scenario
	/// <see cref="ResolveTimeoutApproval"/> guards against for <c>Approve</c>: nothing here can ever
	/// auto-grant tier access on a restart.
	/// </remarks>
	/// <param name="state">The timing-out escalation.</param>
	/// <returns>The resolution type, approval verdict, and escalated-to tier (if any) to record.</returns>
	private (EscalationResolutionType ResolutionType, bool IsApproved, AutonomyLevel? EscalatedToTier)
		ResolveTimeoutOutcome(EscalationState state)
	{
		var request = state.Request;
		var escalates = request.TimeoutAction is EscalationTimeoutAction.Escalate
			or EscalationTimeoutAction.DenyAndEscalate;

		if (escalates && request.EscalationTierTarget is { } tier)
			return (EscalationResolutionType.Escalated, false, tier);

		return (EscalationResolutionType.TimedOut, ResolveTimeoutApproval(state), null);
	}

	/// <summary>
	/// Decides the verdict a timeout produces, downgrading <c>Approve</c> to a denial for a
	/// rehydrated escalation whose deadline elapsed while the host was down.
	/// </summary>
	/// <remarks>
	/// <b>The fail-open this closes.</b> A 5-minute escalation queued just before a 30-minute
	/// deploy would, on restart, fire its timeout microseconds after rehydration — granting
	/// <c>TimeoutAction.Approve</c> before any approver could possibly have seen it, and
	/// rehydration deliberately re-sends no notifications, so none ever would. Pre-durability a
	/// restart merely lost the escalation; auto-approving it is a strictly worse outcome that
	/// this feature would otherwise introduce.
	/// <para>
	/// The chosen rule is <b>deny</b>, not a grace window: a grace window still auto-approves
	/// on the second restart, and re-notifying would need a notification-idempotency story the
	/// notifier does not have. Denying is fail-closed, needs no new state, and the requester
	/// can re-raise the escalation — which re-notifies naturally.
	/// </para>
	/// </remarks>
	/// <param name="state">The timing-out escalation.</param>
	/// <returns>Whether the timeout grants approval.</returns>
	/// <remarks>
	/// Deliberately stays boolean, not the three-way <see cref="ApproverVerdict"/>: a timeout is
	/// silence, and silence can only ever mean approve or deny per <see cref="EscalationTimeoutAction"/>
	/// — there is no reviewer present to have asked for a revision, so a timeout can never produce
	/// <see cref="EscalationResolutionType.Revised"/>.
	/// </remarks>
	private bool ResolveTimeoutApproval(EscalationState state)
	{
		var wouldApprove = state.Request.TimeoutAction == EscalationTimeoutAction.Approve;

		// Consults the decision captured at restore rather than re-testing the deadline now:
		// by the time this runs the delay has already elapsed, so a re-test is subject to timer
		// granularity and clock steps and can wrongly report the deadline as still in future.
		if (!wouldApprove || !state.ExpiredDuringDowntime)
			return wouldApprove;

		_logger.LogWarning(
			"Rehydrated escalation {EscalationId} expired during downtime; downgrading TimeoutAction.Approve " +
			"to a denial because no approver could have seen it and rehydration re-sends no notifications",
			state.Request.EscalationId);
		return false;
	}

	private void CleanupCancelledEscalation(EscalationState state)
	{
		lock (state.Lock)
		{
			if (state.IsResolved)
				return;
			state.IsResolved = true;
		}

		_activeEscalations.TryRemove(state.Request.EscalationId, out _);
		state.TimeoutCts.Cancel();
		state.Completion.TrySetCanceled();
		state.DisposeSynchronizationPrimitives();
		EscalationMetrics.Pending.Add(-1);

		// Best-effort durable removal (no-op with the Null store): the blocking caller
		// abandoned the escalation, so its record should not rehydrate. If the removal fails,
		// the leftover Pending row rehydrates on the next start and resolves via its original
		// timeout — noisy but safe, and preferable to blocking the caller's cancellation path.
		_ = SafeExecuteAsync(
			() => _stateStore.RemoveAsync(state.Request.EscalationId, CancellationToken.None),
			"remove abandoned escalation state", state.Request.EscalationId);

		_logger.LogWarning("Escalation {EscalationId} cancelled by caller",
			state.Request.EscalationId);
	}

	private void RemoveFailedEscalation(EscalationState state)
	{
		if (_activeEscalations.TryRemove(state.Request.EscalationId, out _))
		{
			state.TimeoutCts.Cancel();
			state.DisposeSynchronizationPrimitives();
			EscalationMetrics.Pending.Add(-1);
		}
	}

	private static void RecordResolutionMetrics(EscalationState state, EscalationOutcome outcome)
	{
		var durationMs = (outcome.ResolvedAt - state.CreatedAt).TotalMilliseconds;

		EscalationMetrics.Resolutions.Add(1,
			new KeyValuePair<string, object?>(EscalationConventions.ResolutionType,
				ToResolutionTag(outcome.ResolutionType)),
			new KeyValuePair<string, object?>(EscalationConventions.Priority,
				ToPriorityTag(state.Request.Priority)));

		EscalationMetrics.DurationMs.Record(durationMs,
			new KeyValuePair<string, object?>(EscalationConventions.Priority,
				ToPriorityTag(state.Request.Priority)));
	}
}
