using System.Collections.Concurrent;
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
/// Process restart loses pending state. The <see cref="IEscalationAuditStore"/> provides
/// durable compliance records, but automatic recovery from audit logs is not
/// implemented (Phase 3+).
/// </para>
/// <para>
/// Resolved outcomes are also retained in memory (see <see cref="_resolvedOutcomes"/>) so callers
/// such as the plan executor can query a verdict via <see cref="GetOutcomeAsync"/> after the
/// escalation has left the active set. Retention is process-lifetime and consistent with the
/// active-state model above: a restart that loses a pending escalation would equally lose its
/// resolved outcome, and such an escalation could never have been approved post-restart anyway.
/// Escalations are human-scale, low-frequency events, so this retention does not grow unbounded in
/// practice.
/// </para>
/// </remarks>
public sealed class DefaultEscalationService : IEscalationService, IDisposable
{
	private readonly ConcurrentDictionary<Guid, EscalationState> _activeEscalations = new();
	private readonly ConcurrentDictionary<Guid, EscalationOutcome> _resolvedOutcomes = new();
	private readonly IServiceProvider _serviceProvider;
	private readonly IEscalationNotifier _notifier;
	private readonly IEscalationAuditStore _auditStore;
	private readonly IOptionsMonitor<EscalationConfig> _config;
	private readonly ILogger<DefaultEscalationService> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="DefaultEscalationService"/> class.
	/// </summary>
	/// <param name="serviceProvider">Service provider for resolving keyed <see cref="IApprovalStrategy"/> instances.</param>
	/// <param name="notifier">Fan-out notification dispatcher for escalation events.</param>
	/// <param name="auditStore">Durable audit trail for compliance recording.</param>
	/// <param name="config">Escalation configuration (defaults, priority overrides).</param>
	/// <param name="logger">Structured logger.</param>
	public DefaultEscalationService(
		IServiceProvider serviceProvider,
		IEscalationNotifier notifier,
		IEscalationAuditStore auditStore,
		IOptionsMonitor<EscalationConfig> config,
		ILogger<DefaultEscalationService> logger)
	{
		_serviceProvider = serviceProvider;
		_notifier = notifier;
		_auditStore = auditStore;
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

		var strategy = _serviceProvider.GetRequiredKeyedService<IApprovalStrategy>(state.Request.ApprovalStrategy);
		EscalationOutcome? outcome;

		lock (state.Lock)
		{
			// Recorded for audit above, but another decision resolved the escalation
			// concurrently — this one arrives too late to participate. The caller polls
			// GetOutcomeAsync for the verdict (see EscalationDecisionStatus.DecisionRecorded).
			if (state.IsResolved)
				return EscalationDecisionResult.DecisionRecorded();

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
			var evaluation = strategy.EvaluateDecision(state.Request, state.Decisions.AsReadOnly());

			_logger.LogDebug(
				"Strategy evaluation for {EscalationId}: IsResolved={IsResolved}, IsApproved={IsApproved}",
				escalationId, evaluation.IsResolved, evaluation.IsApproved);

			if (!evaluation.IsResolved)
				return EscalationDecisionResult.DecisionRecorded();

			state.IsResolved = true;
			outcome = new EscalationOutcome
			{
				EscalationId = escalationId,
				IsApproved = evaluation.IsApproved,
				Decisions = state.Decisions.ToList().AsReadOnly(),
				ResolutionType = evaluation.IsApproved
					? EscalationResolutionType.Approved
					: EscalationResolutionType.Denied,
				ResolvedAt = DateTimeOffset.UtcNow,
				Approvers = state.Request.Approvers
			};
		}

		await ResolveEscalationAsync(state, outcome);
		return EscalationDecisionResult.Resolved(outcome);
	}

	/// <inheritdoc />
	public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct)
	{
		_activeEscalations.TryGetValue(escalationId, out var state);
		return Task.FromResult<EscalationRequest?>(state?.Request);
	}

	/// <inheritdoc />
	public Task<EscalationOutcome?> GetOutcomeAsync(Guid escalationId, CancellationToken ct)
	{
		_resolvedOutcomes.TryGetValue(escalationId, out var outcome);
		return Task.FromResult(outcome);
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
	public async Task<EscalationOutcome> CancelEscalationAsync(
		Guid escalationId, string reason, string cancelledBy, CancellationToken ct)
	{
		if (!_activeEscalations.TryGetValue(escalationId, out var state))
			throw new InvalidOperationException($"No pending escalation found with ID {escalationId}");

		EscalationOutcome outcome;
		lock (state.Lock)
		{
			if (state.IsResolved)
				throw new InvalidOperationException($"Escalation {escalationId} is already resolved");

			state.IsResolved = true;
			outcome = new EscalationOutcome
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
		}

		_logger.LogInformation(
			"Escalation {EscalationId} cancelled by {CancelledBy}: {Reason}",
			escalationId, cancelledBy, reason);
		await ResolveEscalationAsync(state, outcome);
		return outcome;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (var state in _activeEscalations.Values)
		{
			state.Completion.TrySetCanceled();
			state.TimeoutCts.Cancel();
			state.TimeoutCts.Dispose();
		}
		_activeEscalations.Clear();
	}

	private EscalationState InitializeEscalation(EscalationRequest request)
	{
		if (request.Approvers.Count == 0)
		{
			// Fail closed at creation: an escalation with no approver roster can never be
			// legitimately approved. Admitting it would let the AllOf strategy treat "nobody
			// pending" as vacuously unanimous, or the timeout Approve action grant it silently.
			_logger.LogWarning(
				"Rejected escalation {EscalationId} for agent {AgentId}: empty approver roster",
				request.EscalationId, request.AgentId);
			throw new InvalidOperationException(
				$"Escalation {request.EscalationId} has no approvers; refusing to create an escalation that cannot be approved.");
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

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationRequestedAsync(state.Request, ct),
			"notify request", state.Request.EscalationId);
	}

	private void RemoveFailedEscalation(EscalationState state)
	{
		if (_activeEscalations.TryRemove(state.Request.EscalationId, out _))
		{
			state.TimeoutCts.Cancel();
			state.TimeoutCts.Dispose();
			EscalationMetrics.Pending.Add(-1);
		}
	}

	private async Task ResolveEscalationAsync(EscalationState state, EscalationOutcome outcome)
	{
		// Idempotency / teardown guard: if the completion was already settled
		// (e.g. the service was disposed and cancelled it), don't re-run cleanup.
		// Each caller (SubmitDecision/Cancel/Timeout) sets IsResolved under the
		// state lock before calling here, so this runs at most once per escalation.
		if (state.Completion.Task.IsCompleted)
			return;

		state.TimeoutCts.Cancel();

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
			state.Completion.TrySetException(ex);
			throw;
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
			await Task.Delay(
				TimeSpan.FromSeconds(state.Request.TimeoutSeconds),
				state.TimeoutCts.Token);

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
		EscalationOutcome? outcome;

		lock (state.Lock)
		{
			if (state.IsResolved)
				return;

			state.IsResolved = true;

			outcome = new EscalationOutcome
			{
				EscalationId = state.Request.EscalationId,
				IsApproved = state.Request.TimeoutAction == EscalationTimeoutAction.Approve,
				Decisions = state.Decisions.ToList().AsReadOnly(),
				ResolutionType = EscalationResolutionType.TimedOut,
				ResolvedAt = DateTimeOffset.UtcNow,
				Approvers = state.Request.Approvers
			};
		}

		EscalationMetrics.Timeouts.Add(1,
			new KeyValuePair<string, object?>(EscalationConventions.Priority,
				ToPriorityTag(state.Request.Priority)));

		_logger.LogWarning(
			"Escalation {EscalationId} timed out with action {TimeoutAction}",
			state.Request.EscalationId, state.Request.TimeoutAction);

		await ResolveEscalationAsync(state, outcome);
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
		EscalationMetrics.Pending.Add(-1);
		_logger.LogWarning("Escalation {EscalationId} cancelled by caller",
			state.Request.EscalationId);
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

	/// <summary>
	/// Returns this approver's already-recorded decision on the escalation, or null when none
	/// exists, using <see cref="ApproverNames.Comparer"/> so a casing-variant retry is still a
	/// duplicate. Takes the state lock itself, so callers may invoke it lock-free; the in-lock
	/// re-check in <see cref="SubmitDecisionAsync"/> relies on the lock being reentrant for the
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
		public readonly object Lock = new();
	}
}
