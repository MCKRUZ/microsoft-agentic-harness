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
/// </remarks>
public sealed class DefaultEscalationService : IEscalationService, IDisposable
{
	private readonly ConcurrentDictionary<Guid, EscalationState> _activeEscalations = new();
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
		await RecordAndNotifyRequestAsync(state, ct);
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
		await RecordAndNotifyRequestAsync(state, ct);
		_ = RunTimeoutAsync(state);
		return request.EscalationId;
	}

	/// <inheritdoc />
	public async Task<EscalationOutcome?> SubmitDecisionAsync(
		Guid escalationId, ApproverDecision decision, CancellationToken ct)
	{
		if (!_activeEscalations.TryGetValue(escalationId, out var state))
		{
			_logger.LogWarning("Decision submitted for unknown escalation {EscalationId}", escalationId);
			return null;
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
			if (state.IsResolved)
				return null;

			state.Decisions.Add(decision);
			var evaluation = strategy.EvaluateDecision(state.Request, state.Decisions.AsReadOnly());

			_logger.LogDebug(
				"Strategy evaluation for {EscalationId}: IsResolved={IsResolved}, IsApproved={IsApproved}",
				escalationId, evaluation.IsResolved, evaluation.IsApproved);

			if (!evaluation.IsResolved)
				return null;

			state.IsResolved = true;
			outcome = new EscalationOutcome
			{
				EscalationId = escalationId,
				IsApproved = evaluation.IsApproved,
				Decisions = state.Decisions.ToList().AsReadOnly(),
				ResolutionType = evaluation.IsApproved
					? EscalationResolutionType.Approved
					: EscalationResolutionType.Denied,
				ResolvedAt = DateTimeOffset.UtcNow
			};
		}

		await ResolveEscalationAsync(state, outcome);
		return outcome;
	}

	/// <inheritdoc />
	public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct)
	{
		_activeEscalations.TryGetValue(escalationId, out var state);
		return Task.FromResult<EscalationRequest?>(state?.Request);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(
		string approverName, CancellationToken ct)
	{
		var pending = _activeEscalations.Values
			.Where(s => s.Request.Approvers.Contains(approverName))
			.Select(s => s.Request)
			.ToList();
		return Task.FromResult<IReadOnlyList<EscalationRequest>>(pending.AsReadOnly());
	}

	/// <inheritdoc />
	public async Task<EscalationOutcome> CancelEscalationAsync(
		Guid escalationId, string reason, CancellationToken ct)
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
				ResolvedAt = DateTimeOffset.UtcNow
			};
		}

		_logger.LogInformation("Escalation {EscalationId} cancelled: {Reason}", escalationId, reason);
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
		await SafeExecuteAsync(
			() => _auditStore.RecordRequestAsync(state.Request, ct),
			"record request", state.Request.EscalationId);

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationRequestedAsync(state.Request, ct),
			"notify request", state.Request.EscalationId);
	}

	private async Task ResolveEscalationAsync(EscalationState state, EscalationOutcome outcome)
	{
		if (!state.Completion.TrySetResult(outcome))
			return;

		state.TimeoutCts.Cancel();
		_activeEscalations.TryRemove(state.Request.EscalationId, out _);

		EscalationMetrics.Pending.Add(-1);
		RecordResolutionMetrics(state, outcome);

		_logger.LogInformation(
			"Escalation {EscalationId} resolved: {ResolutionType}, approved={IsApproved}",
			outcome.EscalationId, outcome.ResolutionType, outcome.IsApproved);

		await SafeExecuteAsync(
			() => _auditStore.RecordOutcomeAsync(outcome, CancellationToken.None),
			"record outcome", outcome.EscalationId);

		await SafeExecuteAsync(
			() => _notifier.NotifyEscalationResolvedAsync(outcome, CancellationToken.None),
			"notify resolution", outcome.EscalationId);
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
				ResolvedAt = DateTimeOffset.UtcNow
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
