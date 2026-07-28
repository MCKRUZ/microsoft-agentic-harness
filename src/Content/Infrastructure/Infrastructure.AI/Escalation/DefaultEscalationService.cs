using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Escalation;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Orchestrates the escalation lifecycle: creation, approval tracking,
/// timeout management, notification dispatch, and audit recording.
/// </summary>
/// <remarks>
/// <para>
/// Active escalations are held in memory via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// The <see cref="IEscalationAuditStore"/> provides durable compliance records; the
/// <see cref="IEscalationStateStore"/> provides durable <i>working</i> state. With the default
/// <c>NullEscalationStateStore</c> (durability off), process restart loses pending state — the
/// original in-memory contract, byte-for-byte. With the EF-backed store
/// (<c>AppConfig:AI:Governance:DurableState:EscalationsEnabled</c>), every lifecycle transition
/// is durably recorded fail-closed, and <see cref="RehydratePendingEscalationsAsync"/> restores
/// pending escalations on startup as decidable, listable, and cancellable.
/// </para>
/// <para>
/// What durability cannot restore: the <see cref="TaskCompletionSource{TResult}"/> waiter a
/// blocking <see cref="RequestEscalationAsync"/> caller holds is inherently per-process. After
/// a restart no code is released by the eventual decision — resumed workflows poll
/// <see cref="GetOutcomeAsync"/> (which falls back to the durable store) for the verdict, as the
/// plan executor's resume path already does.
/// </para>
/// <para>
/// Resolved outcomes are also retained in memory (see <see cref="_resolvedOutcomes"/>) so callers
/// such as the plan executor can query a verdict via <see cref="GetOutcomeAsync"/> after the
/// escalation has left the active set. Retention is process-lifetime; with durability enabled the
/// durable store additionally serves outcomes resolved in previous process lifetimes.
/// Escalations are human-scale, low-frequency events, so this retention does not grow unbounded in
/// practice.
/// </para>
/// <para>
/// The file is split by responsibility: this partial owns the public lifecycle surface,
/// <c>DefaultEscalationService.Resolution.cs</c> owns resolution/timeout/teardown, and
/// <c>DefaultEscalationService.Durability.cs</c> owns rehydration and the
/// <see cref="IEscalationReconciler"/> recovery path for the audit-outage stuck state.
/// </para>
/// </remarks>
public sealed partial class DefaultEscalationService : IEscalationService, IEscalationReconciler, IDisposable
{
	private readonly ConcurrentDictionary<Guid, EscalationState> _activeEscalations = new();
	private readonly ConcurrentDictionary<Guid, EscalationOutcome> _resolvedOutcomes = new();
	private readonly SemaphoreSlim _reconcileGate = new(1, 1);
	private int _disposed;
	private readonly IServiceProvider _serviceProvider;
	private readonly IEscalationNotifier _notifier;
	private readonly IEscalationAuditStore _auditStore;
	private readonly IEscalationStateStore _stateStore;
	private readonly IOptionsMonitor<EscalationConfig> _config;
	private readonly ILogger<DefaultEscalationService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="DefaultEscalationService"/> class.
	/// </summary>
	/// <param name="serviceProvider">Service provider for resolving keyed <see cref="IApprovalStrategy"/> instances.</param>
	/// <param name="notifier">Fan-out notification dispatcher for escalation events.</param>
	/// <param name="auditStore">Durable audit trail for compliance recording.</param>
	/// <param name="stateStore">
	/// Durable working-state store. The composition root supplies the no-op
	/// <c>NullEscalationStateStore</c> when durable escalation state is disabled (the default),
	/// which preserves in-memory-only behavior exactly.
	/// </param>
	/// <param name="config">Escalation configuration (defaults, priority overrides).</param>
	/// <param name="logger">Structured logger.</param>
	public DefaultEscalationService(
		IServiceProvider serviceProvider,
		IEscalationNotifier notifier,
		IEscalationAuditStore auditStore,
		IEscalationStateStore stateStore,
		IOptionsMonitor<EscalationConfig> config,
		ILogger<DefaultEscalationService> logger)
	{
		_serviceProvider = serviceProvider;
		_notifier = notifier;
		_auditStore = auditStore;
		_stateStore = stateStore;
		_config = config;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<EscalationOutcome> RequestEscalationAsync(
		EscalationRequest request, CancellationToken ct)
	{
		var state = InitializeEscalation(request);
		try
		{
			await RecordAndNotifyRequestAsync(state, ct);
		}
		catch
		{
			RemoveFailedEscalation(state);
			throw;
		}
		_ = RunTimeoutAsync(state);

		try
		{
			return await state.Completion.Task.WaitAsync(ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			CleanupCancelledEscalation(state);
			throw;
		}
	}

	/// <inheritdoc />
	public async Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct)
	{
		var state = InitializeEscalation(request);
		try
		{
			await RecordAndNotifyRequestAsync(state, ct);
		}
		catch
		{
			RemoveFailedEscalation(state);
			throw;
		}
		_ = RunTimeoutAsync(state);
		return request.EscalationId;
	}

	/// <inheritdoc />
	public async Task<EscalationDecisionResult> SubmitDecisionAsync(
		Guid escalationId, ApproverDecision decision, CancellationToken ct)
	{
		if (!_activeEscalations.TryGetValue(escalationId, out var state))
		{
			_logger.LogWarning("Decision submitted for unknown escalation {EscalationId}", escalationId);
			return EscalationDecisionResult.UnknownEscalation();
		}

		// Authorization chokepoint: reject decisions from identities outside the approver
		// roster before they are recorded, evaluated, or allowed to resolve the escalation.
		// The strategies also filter non-roster votes (defense in depth), but stopping here
		// keeps unauthorized decisions out of the audit trail and strategy evaluation entirely.
		if (!state.Request.Approvers.Contains(decision.ApproverName, ApproverNames.Comparer))
		{
			_logger.LogWarning(
				"Rejected decision from non-roster identity {ApproverName} for escalation {EscalationId}",
				decision.ApproverName, escalationId);
			return EscalationDecisionResult.ApproverNotAuthorized();
		}

		// Idempotency chokepoint: one recorded decision per approver per escalation. A repeated
		// submission (double-click, HTTP retry, replay) must not append to the decision list or
		// grow the audit trail — the first decision already speaks for this approver. Checked
		// BEFORE the audit write so a repeat produces a log line, never an audit record. A
		// repeat with the SAME verdict echoes DecisionRecorded (idempotent); a repeat with the
		// OPPOSITE verdict is a conflict — silently dropping a changed vote while reporting it
		// recorded would be dishonest, and recording it would let a replay flip a final vote.
		if (TryGetExistingDecision(state, decision.ApproverName) is { } existing)
		{
			if (existing.Approved != decision.Approved)
			{
				_logger.LogWarning(
					"Conflicting decision from {ApproverName} for escalation {EscalationId} rejected: {New} contradicts recorded {Recorded}",
					decision.ApproverName, escalationId, decision.Approved, existing.Approved);
				return EscalationDecisionResult.ConflictingDecision();
			}

			_logger.LogDebug(
				"Duplicate decision from {ApproverName} for escalation {EscalationId} ignored (already recorded)",
				decision.ApproverName, escalationId);
			return EscalationDecisionResult.DecisionRecorded();
		}

		await SafeExecuteAsync(
			() => _auditStore.RecordDecisionAsync(escalationId, decision, ct),
			"record decision", escalationId);

		var elapsed = DateTimeOffset.UtcNow - state.CreatedAt;
		EscalationMetrics.ApproverResponseMs.Record(elapsed.TotalMilliseconds,
			new KeyValuePair<string, object?>(EscalationConventions.ApproverName, decision.ApproverName));

		return await ApplyDecisionAsync(state, decision, ct);
	}

	/// <summary>
	/// Applies a vetted decision: mutates the in-memory decision list, evaluates the strategy,
	/// and durably persists the new list — all inside the per-escalation write gate.
	/// </summary>
	/// <remarks>
	/// The gate is what makes "compute the snapshot" and "persist the snapshot" atomic with
	/// respect to other submissions on the same escalation. Without it, two concurrent
	/// non-resolving approvals under AllOf can each build a snapshot inside the lock, release,
	/// and then write out of order: last-write-wins persists the shorter list, so a restart
	/// silently loses an approval that in-memory state still shows. Serializing per escalation
	/// (not globally) keeps unrelated escalations independent.
	/// </remarks>
	/// <param name="state">The active escalation.</param>
	/// <param name="decision">The decision to apply.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task<EscalationDecisionResult> ApplyDecisionAsync(
		EscalationState state, ApproverDecision decision, CancellationToken ct)
	{
		var strategy = _serviceProvider.GetRequiredKeyedService<IApprovalStrategy>(state.Request.ApprovalStrategy);

		// The gate can be disposed underneath us by a teardown (caller cancellation, service
		// disposal) that raced the active-set lookup in SubmitDecisionAsync. Report the
		// escalation as unknown — exactly what a lookup a moment later would say — rather than
		// letting an ObjectDisposedException escape to the approver as a 500.
		if (!await TryEnterWriteGateAsync(state, ct))
		{
			_logger.LogWarning(
				"Escalation {EscalationId} was torn down while a decision from {ApproverName} was in flight; " +
				"reporting it unknown",
				state.Request.EscalationId, decision.ApproverName);
			return EscalationDecisionResult.UnknownEscalation();
		}

		try
		{
			return await ApplyDecisionUnderGateAsync(state, decision, strategy, ct);
		}
		finally
		{
			ReleaseWriteGate(state);
		}
	}

	/// <summary>
	/// The body of <see cref="ApplyDecisionAsync"/>, run with the escalation's write gate held.
	/// </summary>
	/// <param name="state">The active escalation.</param>
	/// <param name="decision">The decision to apply.</param>
	/// <param name="strategy">The approval strategy for this escalation.</param>
	/// <param name="ct">Cancellation token.</param>
	private async Task<EscalationDecisionResult> ApplyDecisionUnderGateAsync(
		EscalationState state, ApproverDecision decision, IApprovalStrategy strategy, CancellationToken ct)
	{
		EscalationOutcome? outcome;
		IReadOnlyList<ApproverDecision> decisionsSnapshot;

		lock (state.Lock)
		{
			// A verdict was already reached without this decision. If the resolution is
			// parked on a failed durable/audit write, say so plainly rather than reporting
			// a vote recorded that was in fact discarded.
			if (state.IsResolved)
			{
				return state.ResolutionFailed
					? EscalationDecisionResult.AwaitingReconciliation()
					: EscalationDecisionResult.DecisionRecorded();
			}

			// Closes the race two concurrent submissions from the same approver can win against
			// the pre-audit duplicate check: only the first may enter the decision list, and a
			// contradicting verdict is a conflict here exactly as at the chokepoint above.
			if (TryGetExistingDecision(state, decision.ApproverName) is { } raced)
			{
				return raced.Approved != decision.Approved
					? EscalationDecisionResult.ConflictingDecision()
					: EscalationDecisionResult.DecisionRecorded();
			}

			state.Decisions.Add(decision);
			decisionsSnapshot = state.Decisions.ToList().AsReadOnly();
			outcome = EvaluateResolution(state, strategy);
		}

		// Fail-closed durable decision write (no-op with the Null store): the decision must
		// be durably recorded before it is reported recorded.
		try
		{
			await _stateStore.SaveDecisionsAsync(state.Request.EscalationId, decisionsSnapshot, ct);
		}
		catch (Exception ex)
		{
			RollBackDecision(state, decision);
			_logger.LogError(ex,
				"Durable decision write failed for escalation {EscalationId}; failing closed (decision not recorded)",
				state.Request.EscalationId);
			throw new EscalationDurableStateException(
				EscalationDurableStateException.DurableWriteFailedCode, ex);
		}

		if (outcome is null)
			return EscalationDecisionResult.DecisionRecorded();

		await ResolveEscalationAsync(state, outcome);
		return EscalationDecisionResult.Resolved(outcome);
	}

	/// <summary>
	/// Acquires an escalation's write gate, reporting false instead of throwing when a
	/// concurrent teardown has already disposed it.
	/// </summary>
	/// <remarks>
	/// <see cref="CleanupCancelledEscalation"/> and <see cref="Dispose"/> dispose the gate while
	/// other paths may be between their active-set lookup and this acquire. Every holder — the
	/// decision path, the timeout path, and cancellation — funnels through this helper and
	/// <see cref="ReleaseWriteGate"/> so the race is handled identically in all three.
	/// </remarks>
	/// <param name="state">The escalation whose gate to take.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>True when the gate was acquired; false when it was already disposed.</returns>
	private static async Task<bool> TryEnterWriteGateAsync(EscalationState state, CancellationToken ct)
	{
		try
		{
			await state.WriteGate.WaitAsync(ct);
			return true;
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>
	/// Releases an escalation's write gate, tolerating a teardown that disposed it while the
	/// holder was still working. Never call unless <see cref="TryEnterWriteGateAsync"/> returned
	/// true.
	/// </summary>
	/// <param name="state">The escalation whose gate to release.</param>
	private static void ReleaseWriteGate(EscalationState state)
	{
		try
		{
			state.WriteGate.Release();
		}
		catch (ObjectDisposedException)
		{
			// Torn down while the holder was working; nothing left to release.
		}
	}

	/// <summary>
	/// Evaluates the approval strategy against the current decision list and, when it resolves,
	/// marks the state resolved and stashes the outcome. Must be called under
	/// <see cref="EscalationState.Lock"/>.
	/// </summary>
	/// <param name="state">The active escalation.</param>
	/// <param name="strategy">The approval strategy for this escalation.</param>
	/// <returns>The resolved outcome, or null when the escalation remains pending.</returns>
	private EscalationOutcome? EvaluateResolution(EscalationState state, IApprovalStrategy strategy)
	{
		var evaluation = strategy.EvaluateDecision(state.Request, state.Decisions.AsReadOnly());

		_logger.LogDebug(
			"Strategy evaluation for {EscalationId}: IsResolved={IsResolved}, IsApproved={IsApproved}",
			state.Request.EscalationId, evaluation.IsResolved, evaluation.IsApproved);

		if (!evaluation.IsResolved)
			return null;

		var outcome = new EscalationOutcome
		{
			EscalationId = state.Request.EscalationId,
			IsApproved = evaluation.IsApproved,
			Decisions = state.Decisions.ToList().AsReadOnly(),
			ResolutionType = evaluation.IsApproved
				? EscalationResolutionType.Approved
				: EscalationResolutionType.Denied,
			ResolvedAt = DateTimeOffset.UtcNow,
			Approvers = state.Request.Approvers
		};

		MarkResolving(state, outcome);
		return outcome;
	}

	/// <summary>
	/// Backs the in-memory mutation out after a failed durable decision write, so a retry once
	/// the store recovers replays cleanly instead of being rejected as a duplicate by a ghost
	/// decision the durable store never accepted. Also releases the finalize claim so a
	/// genuinely stuck state stays claimable by the reconciler.
	/// </summary>
	/// <param name="state">The active escalation.</param>
	/// <param name="decision">The decision to remove.</param>
	private static void RollBackDecision(EscalationState state, ApproverDecision decision)
	{
		lock (state.Lock)
		{
			state.Decisions.Remove(decision);
			state.IsResolved = false;
			state.PendingOutcome = null;
			state.FinalizeClaimed = false;
			state.ResolutionFailed = false;
		}
	}

	/// <inheritdoc />
	public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct)
	{
		_activeEscalations.TryGetValue(escalationId, out var state);
		return Task.FromResult<EscalationRequest?>(state?.Request);
	}

	/// <inheritdoc />
	public async Task<EscalationOutcome?> GetOutcomeAsync(Guid escalationId, CancellationToken ct)
	{
		if (_resolvedOutcomes.TryGetValue(escalationId, out var outcome))
			return outcome;

		// Post-restart fallback: verdicts resolved and audited in a previous process lifetime
		// are served from the durable store. The store only surfaces terminally-Resolved
		// records (never ResolvedPendingAudit) and only when their seal verifies, so neither an
		// un-audited nor a tampered outcome is observable. Null store returns null (parity).
		return await _stateStore.GetResolvedOutcomeAsync(escalationId, ct);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(
		string approverName, CancellationToken ct)
	{
		// Match the roster the same way the decide path does: an approver whose identity
		// differs from the roster entry only by casing must see the escalations they are
		// allowed to decide. ApproverNames.Comparer is the single source of that rule.
		var pending = _activeEscalations.Values
			.Where(s => s.Request.Approvers.Contains(approverName, ApproverNames.Comparer))
			.Select(s => s.Request)
			.ToList();
		return Task.FromResult<IReadOnlyList<EscalationRequest>>(pending.AsReadOnly());
	}

	/// <inheritdoc />
	/// <remarks>
	/// Takes the per-escalation write gate, exactly as the decision and timeout paths do.
	/// Cancellation <em>resolves</em> an escalation, so running it concurrently with a decision
	/// corrupts the resolution bookkeeping: a cancel that marks the state resolving while a
	/// decision's durable write is in flight has that bookkeeping wiped by the write's failure
	/// path (<see cref="RollBackDecision"/> clears <c>IsResolved</c>/<c>PendingOutcome</c>),
	/// stranding the durable <c>ResolvedPendingAudit</c> row the cancel already wrote — the
	/// in-memory reconcile shape skips it because <c>IsResolved</c> is false, the durable shape
	/// skips it because the id is still in the active set, and the pruner correctly refuses to
	/// delete a non-terminal row. Serializing on the gate removes the interleaving entirely.
	/// <para>
	/// Lock ordering is unchanged and deadlock-free: every holder takes the gate first and
	/// <c>state.Lock</c> second, and no path holds <c>state.Lock</c> while waiting on the gate.
	/// </para>
	/// </remarks>
	public async Task<EscalationOutcome> CancelEscalationAsync(
		Guid escalationId, string reason, string cancelledBy, CancellationToken ct)
	{
		if (!_activeEscalations.TryGetValue(escalationId, out var state))
			throw new InvalidOperationException($"No pending escalation found with ID {escalationId}");

		// A disposed gate means the escalation was torn down between the lookup and here; it is
		// no longer pending, which is the same answer the lookup above would now give.
		if (!await TryEnterWriteGateAsync(state, ct))
			throw new InvalidOperationException($"No pending escalation found with ID {escalationId}");

		try
		{
			var outcome = BuildCancellationOutcome(state, escalationId, cancelledBy);

			_logger.LogInformation(
				"Escalation {EscalationId} cancelled by {CancelledBy}: {Reason}",
				escalationId, cancelledBy, reason);
			await ResolveEscalationAsync(state, outcome);
			return outcome;
		}
		finally
		{
			ReleaseWriteGate(state);
		}
	}

	/// <summary>
	/// Builds the force-denial outcome for a cancellation and marks the state resolving, under
	/// the state lock. Callers must already hold the escalation's write gate.
	/// </summary>
	/// <param name="state">The escalation being cancelled.</param>
	/// <param name="escalationId">The escalation id (for the failure message).</param>
	/// <param name="cancelledBy">The actor performing the cancellation.</param>
	/// <returns>The denial outcome to drive through resolution.</returns>
	/// <exception cref="InvalidOperationException">The escalation is already resolved.</exception>
	private static EscalationOutcome BuildCancellationOutcome(
		EscalationState state, Guid escalationId, string cancelledBy)
	{
		lock (state.Lock)
		{
			if (state.IsResolved)
				throw new InvalidOperationException($"Escalation {escalationId} is already resolved");

			var outcome = new EscalationOutcome
			{
				EscalationId = escalationId,
				IsApproved = false,
				Decisions = state.Decisions.ToList().AsReadOnly(),
				ResolutionType = EscalationResolutionType.Denied,
				ResolvedAt = DateTimeOffset.UtcNow,
				Approvers = state.Request.Approvers,
				// Stamped on the outcome — and thereby into the durable outcome audit record —
				// so the force-denial is attributable to its actor, not just a log line.
				CancelledBy = cancelledBy
			};
			MarkResolving(state, outcome);
			return outcome;
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// Waits briefly for an in-flight reconcile pass to finish before disposing its gate: a
	/// pass mid-<c>RecordOutcomeAsync</c> holds the gate, and disposing underneath it would
	/// surface as an <see cref="ObjectDisposedException"/> from the release in its finally
	/// block, masking the real shutdown. If the wait times out the gate is left undisposed —
	/// a bounded leak on a shutting-down process is strictly better than tearing down a
	/// compliance write mid-flight.
	/// </remarks>
	public void Dispose()
	{
		// Idempotent: a singleton can be disposed by both the container and a test fixture,
		// and the reconcile-gate wait below would throw on a second pass.
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		foreach (var state in _activeEscalations.Values)
		{
			state.Completion.TrySetCanceled();
			state.TimeoutCts.Cancel();
			state.DisposeSynchronizationPrimitives();
		}
		_activeEscalations.Clear();

		if (_reconcileGate.Wait(TimeSpan.FromSeconds(5)))
		{
			_reconcileGate.Release();
			_reconcileGate.Dispose();
		}
		else
		{
			_logger.LogWarning(
				"A reconcile pass was still running at shutdown; leaving its gate undisposed rather than " +
				"faulting the in-flight pass");
		}
	}

	private EscalationState InitializeEscalation(EscalationRequest request)
	{
		// Fail closed at creation. The same invariants gate the durable rehydration path, so a
		// hand-edited row can never produce a live escalation the creation path would refuse.
		if (!EscalationRequestInvariants.TryValidate(request, out var violation))
		{
			_logger.LogWarning(
				"Rejected escalation {EscalationId} for agent {AgentId}: {Violation}",
				request.EscalationId, request.AgentId, violation);
			throw new InvalidOperationException(
				$"Escalation {request.EscalationId} is invalid: {violation}.");
		}

		var state = new EscalationState
		{
			Request = request,
			TimeoutCts = new CancellationTokenSource(),
			CreatedAt = DateTimeOffset.UtcNow
		};

		if (!_activeEscalations.TryAdd(request.EscalationId, state))
			throw new InvalidOperationException($"Escalation {request.EscalationId} already exists");

		EscalationMetrics.Requests.Add(1,
			new KeyValuePair<string, object?>(EscalationConventions.AgentId, request.AgentId),
			new KeyValuePair<string, object?>(EscalationConventions.Priority, ToPriorityTag(request.Priority)),
			new KeyValuePair<string, object?>(EscalationConventions.Strategy, ToStrategyTag(request.ApprovalStrategy)));

		EscalationMetrics.Pending.Add(1);

		_logger.LogInformation(
			"Escalation {EscalationId} created for agent {AgentId}, tool {ToolName}, priority {Priority}",
			request.EscalationId, request.AgentId, request.ToolName, request.Priority);

		return state;
	}

	private async Task RecordAndNotifyRequestAsync(EscalationState state, CancellationToken ct)
	{
		// Durable request audit is fail-CLOSED: refuse to open an approvable escalation that
		// could not be recorded for compliance. If it throws, the caller cleans up the
		// half-created escalation and surfaces the failure. Notification stays best-effort.
		await _auditStore.RecordRequestAsync(state.Request, ct);

		// Durable working-state write, equally fail-closed: an escalation that cannot be
		// durably created must not open, or a restart would silently strand an approvable
		// escalation. No-op with the Null store (durability disabled).
		try
		{
			await _stateStore.SavePendingAsync(state.Request, state.CreatedAt, ct);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex,
				"Durable create failed for escalation {EscalationId}; failing closed (escalation not opened)",
				state.Request.EscalationId);
			throw new EscalationDurableStateException(
				EscalationDurableStateException.DurableCreateFailedCode, ex);
		}

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationRequestedAsync(state.Request, ct),
			"notify request", state.Request.EscalationId);
	}

	/// <summary>
	/// Returns this approver's already-recorded decision on the escalation, or null when none
	/// exists, using <see cref="ApproverNames.Comparer"/> so a casing-variant retry is still a
	/// duplicate. Takes the state lock itself, so callers may invoke it lock-free; the in-lock
	/// re-check in <see cref="ApplyDecisionAsync"/> relies on the lock being reentrant for the
	/// same thread.
	/// </summary>
	private static ApproverDecision? TryGetExistingDecision(EscalationState state, string approverName)
	{
		lock (state.Lock)
		{
			return state.Decisions.FirstOrDefault(d =>
				ApproverNames.Comparer.Equals(d.ApproverName, approverName));
		}
	}

	private async Task SafeExecuteAsync(Func<Task> action, string operationName, Guid escalationId)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to {Operation} for escalation {EscalationId}",
				operationName, escalationId);
		}
	}

	private static string ToPriorityTag(EscalationPriority priority) => priority switch
	{
		EscalationPriority.Informational => EscalationConventions.PriorityValues.Informational,
		EscalationPriority.Blocking => EscalationConventions.PriorityValues.Blocking,
		EscalationPriority.Critical => EscalationConventions.PriorityValues.Critical,
		_ => priority.ToString().ToLowerInvariant()
	};

	private static string ToResolutionTag(EscalationResolutionType resolution) => resolution switch
	{
		EscalationResolutionType.Approved => EscalationConventions.ResolutionValues.Approved,
		EscalationResolutionType.Denied => EscalationConventions.ResolutionValues.Denied,
		EscalationResolutionType.TimedOut => EscalationConventions.ResolutionValues.TimedOut,
		EscalationResolutionType.Escalated => EscalationConventions.ResolutionValues.Escalated,
		_ => resolution.ToString().ToLowerInvariant()
	};

	private static string ToStrategyTag(ApprovalStrategyType strategy) => strategy switch
	{
		ApprovalStrategyType.AnyOf => EscalationConventions.StrategyValues.AnyOf,
		ApprovalStrategyType.AllOf => EscalationConventions.StrategyValues.AllOf,
		ApprovalStrategyType.Quorum => EscalationConventions.StrategyValues.Quorum,
		_ => strategy.ToString().ToLowerInvariant()
	};

	/// <summary>Tracks the mutable state of an active escalation.</summary>
	private sealed class EscalationState
	{
		public required EscalationRequest Request { get; init; }
		public List<ApproverDecision> Decisions { get; } = [];
		public TaskCompletionSource<EscalationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public required CancellationTokenSource TimeoutCts { get; init; }
		public required DateTimeOffset CreatedAt { get; init; }
		public bool IsResolved { get; set; }

		/// <summary>
		/// True when this state was restored from the durable store rather than created in
		/// this process.
		/// </summary>
		public bool WasRehydrated { get; init; }

		/// <summary>
		/// True when this state was rehydrated with its deadline ALREADY elapsed. Evaluated
		/// once, at restore, and never re-derived: re-testing the deadline after the timeout
		/// delay returns would let timer granularity or a backwards clock step report the
		/// deadline as still in the future, skipping the fail-closed downgrade of
		/// <c>TimeoutAction.Approve</c> and auto-approving an escalation no approver was
		/// re-notified about. See <c>ResolveTimeoutApproval</c>.
		/// </summary>
		public bool ExpiredDuringDowntime { get; init; }

		/// <summary>
		/// The resolved outcome, stashed when <see cref="IsResolved"/> flips true so the
		/// reconciler can re-drive a resolution whose fail-closed durable/audit writes faulted.
		/// Null while the escalation is genuinely pending.
		/// </summary>
		public EscalationOutcome? PendingOutcome { get; set; }

		/// <summary>
		/// True while some path owns finalization of this resolution. Set in the SAME lock
		/// block that sets <see cref="IsResolved"/>, so a reconcile pass can never claim a
		/// resolution the live path is still driving — the window that otherwise lets a pass
		/// finalize and notify while the live path then rolls back and tells the approver their
		/// vote was NOT recorded. Cleared on rollback and on each resolution failure branch, so
		/// a genuinely stuck state becomes claimable.
		/// </summary>
		public bool FinalizeClaimed { get; set; }

		/// <summary>
		/// True once a fail-closed durable or audit write failed during resolution, leaving the
		/// escalation resolved-but-unrecorded. Distinguishes "parked awaiting reconciliation"
		/// from a transient mid-resolution race, so a late decision gets an honest answer.
		/// </summary>
		public bool ResolutionFailed { get; set; }

		/// <summary>
		/// Serializes decision mutation plus its durable write for this escalation, making the
		/// snapshot-to-persist step atomic against concurrent submissions.
		/// </summary>
		public SemaphoreSlim WriteGate { get; } = new(1, 1);

		public readonly object Lock = new();

		/// <summary>
		/// Releases both disposable primitives this state owns. Every teardown path must call
		/// this — disposing only <see cref="TimeoutCts"/> leaks the <see cref="WriteGate"/>'s
		/// wait handle for the life of the process.
		/// </summary>
		public void DisposeSynchronizationPrimitives()
		{
			TimeoutCts.Dispose();
			WriteGate.Dispose();
		}
	}
}
