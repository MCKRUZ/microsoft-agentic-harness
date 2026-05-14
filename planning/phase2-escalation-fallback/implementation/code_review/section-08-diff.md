diff --git a/src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs b/src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs
new file mode 100644
index 0000000..5852285
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Escalation/DefaultEscalationService.cs
@@ -0,0 +1,376 @@
+using System.Collections.Concurrent;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Escalation;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.Config.AI.Governance;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Escalation;
+
+/// <summary>
+/// Orchestrates the escalation lifecycle: creation, approval tracking,
+/// timeout management, notification dispatch, and audit recording.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Active escalations are held in memory via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
+/// Process restart loses pending state. The <see cref="IEscalationAuditStore"/> provides
+/// durable compliance records, but automatic recovery from audit logs is not
+/// implemented (Phase 3+).
+/// </para>
+/// </remarks>
+public sealed class DefaultEscalationService : IEscalationService, IDisposable
+{
+	private readonly ConcurrentDictionary<Guid, EscalationState> _activeEscalations = new();
+	private readonly IServiceProvider _serviceProvider;
+	private readonly IEscalationNotifier _notifier;
+	private readonly IEscalationAuditStore _auditStore;
+	private readonly IOptionsMonitor<EscalationConfig> _config;
+	private readonly ILogger<DefaultEscalationService> _logger;
+
+	/// <summary>
+	/// Initializes a new instance of the <see cref="DefaultEscalationService"/> class.
+	/// </summary>
+	/// <param name="serviceProvider">Service provider for resolving keyed <see cref="IApprovalStrategy"/> instances.</param>
+	/// <param name="notifier">Fan-out notification dispatcher for escalation events.</param>
+	/// <param name="auditStore">Durable audit trail for compliance recording.</param>
+	/// <param name="config">Escalation configuration (defaults, priority overrides).</param>
+	/// <param name="logger">Structured logger.</param>
+	public DefaultEscalationService(
+		IServiceProvider serviceProvider,
+		IEscalationNotifier notifier,
+		IEscalationAuditStore auditStore,
+		IOptionsMonitor<EscalationConfig> config,
+		ILogger<DefaultEscalationService> logger)
+	{
+		_serviceProvider = serviceProvider;
+		_notifier = notifier;
+		_auditStore = auditStore;
+		_config = config;
+		_logger = logger;
+	}
+
+	/// <inheritdoc />
+	public async Task<EscalationOutcome> RequestEscalationAsync(
+		EscalationRequest request, CancellationToken ct)
+	{
+		var state = InitializeEscalation(request);
+		await RecordAndNotifyRequestAsync(state, ct);
+		_ = RunTimeoutAsync(state);
+
+		try
+		{
+			return await state.Completion.Task.WaitAsync(ct);
+		}
+		catch (OperationCanceledException) when (ct.IsCancellationRequested)
+		{
+			CleanupCancelledEscalation(state);
+			throw;
+		}
+	}
+
+	/// <inheritdoc />
+	public async Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct)
+	{
+		var state = InitializeEscalation(request);
+		await RecordAndNotifyRequestAsync(state, ct);
+		_ = RunTimeoutAsync(state);
+		return request.EscalationId;
+	}
+
+	/// <inheritdoc />
+	public async Task<EscalationOutcome?> SubmitDecisionAsync(
+		Guid escalationId, ApproverDecision decision, CancellationToken ct)
+	{
+		if (!_activeEscalations.TryGetValue(escalationId, out var state))
+		{
+			_logger.LogWarning("Decision submitted for unknown escalation {EscalationId}", escalationId);
+			return null;
+		}
+
+		await SafeExecuteAsync(
+			() => _auditStore.RecordDecisionAsync(escalationId, decision, ct),
+			"record decision", escalationId);
+
+		var elapsed = DateTimeOffset.UtcNow - state.CreatedAt;
+		EscalationMetrics.ApproverResponseMs.Record(elapsed.TotalMilliseconds,
+			new KeyValuePair<string, object?>(EscalationConventions.ApproverName, decision.ApproverName));
+
+		var strategy = _serviceProvider.GetRequiredKeyedService<IApprovalStrategy>(state.Request.ApprovalStrategy);
+		EscalationOutcome? outcome;
+
+		lock (state.Lock)
+		{
+			if (state.IsResolved)
+				return null;
+
+			state.Decisions.Add(decision);
+			var evaluation = strategy.EvaluateDecision(state.Request, state.Decisions.AsReadOnly());
+
+			_logger.LogDebug(
+				"Strategy evaluation for {EscalationId}: IsResolved={IsResolved}, IsApproved={IsApproved}",
+				escalationId, evaluation.IsResolved, evaluation.IsApproved);
+
+			if (!evaluation.IsResolved)
+				return null;
+
+			state.IsResolved = true;
+			outcome = new EscalationOutcome
+			{
+				EscalationId = escalationId,
+				IsApproved = evaluation.IsApproved,
+				Decisions = state.Decisions.ToList().AsReadOnly(),
+				ResolutionType = evaluation.IsApproved
+					? EscalationResolutionType.Approved
+					: EscalationResolutionType.Denied,
+				ResolvedAt = DateTimeOffset.UtcNow
+			};
+		}
+
+		await ResolveEscalationAsync(state, outcome);
+		return outcome;
+	}
+
+	/// <inheritdoc />
+	public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct)
+	{
+		_activeEscalations.TryGetValue(escalationId, out var state);
+		return Task.FromResult<EscalationRequest?>(state?.Request);
+	}
+
+	/// <inheritdoc />
+	public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(
+		string approverName, CancellationToken ct)
+	{
+		var pending = _activeEscalations.Values
+			.Where(s => s.Request.Approvers.Contains(approverName))
+			.Select(s => s.Request)
+			.ToList();
+		return Task.FromResult<IReadOnlyList<EscalationRequest>>(pending.AsReadOnly());
+	}
+
+	/// <inheritdoc />
+	public async Task<EscalationOutcome> CancelEscalationAsync(
+		Guid escalationId, string reason, CancellationToken ct)
+	{
+		if (!_activeEscalations.TryGetValue(escalationId, out var state))
+			throw new InvalidOperationException($"No pending escalation found with ID {escalationId}");
+
+		var outcome = new EscalationOutcome
+		{
+			EscalationId = escalationId,
+			IsApproved = false,
+			Decisions = state.Decisions.ToList().AsReadOnly(),
+			ResolutionType = EscalationResolutionType.Denied,
+			ResolvedAt = DateTimeOffset.UtcNow
+		};
+
+		_logger.LogInformation("Escalation {EscalationId} cancelled: {Reason}", escalationId, reason);
+		await ResolveEscalationAsync(state, outcome);
+		return outcome;
+	}
+
+	/// <inheritdoc />
+	public void Dispose()
+	{
+		foreach (var state in _activeEscalations.Values)
+		{
+			state.TimeoutCts.Cancel();
+			state.TimeoutCts.Dispose();
+		}
+		_activeEscalations.Clear();
+	}
+
+	private EscalationState InitializeEscalation(EscalationRequest request)
+	{
+		var state = new EscalationState
+		{
+			Request = request,
+			TimeoutCts = new CancellationTokenSource(),
+			CreatedAt = DateTimeOffset.UtcNow
+		};
+
+		if (!_activeEscalations.TryAdd(request.EscalationId, state))
+			throw new InvalidOperationException($"Escalation {request.EscalationId} already exists");
+
+		EscalationMetrics.Requests.Add(1,
+			new KeyValuePair<string, object?>(EscalationConventions.AgentId, request.AgentId),
+			new KeyValuePair<string, object?>(EscalationConventions.Priority, ToPriorityTag(request.Priority)),
+			new KeyValuePair<string, object?>(EscalationConventions.Strategy, ToStrategyTag(request.ApprovalStrategy)));
+
+		EscalationMetrics.Pending.Add(1);
+
+		_logger.LogInformation(
+			"Escalation {EscalationId} created for agent {AgentId}, tool {ToolName}, priority {Priority}",
+			request.EscalationId, request.AgentId, request.ToolName, request.Priority);
+
+		return state;
+	}
+
+	private async Task RecordAndNotifyRequestAsync(EscalationState state, CancellationToken ct)
+	{
+		await SafeExecuteAsync(
+			() => _auditStore.RecordRequestAsync(state.Request, ct),
+			"record request", state.Request.EscalationId);
+
+		await SafeExecuteAsync(
+			() => _notifier.NotifyEscalationRequestedAsync(state.Request, ct),
+			"notify request", state.Request.EscalationId);
+	}
+
+	private async Task ResolveEscalationAsync(EscalationState state, EscalationOutcome outcome)
+	{
+		if (!state.Completion.TrySetResult(outcome))
+			return;
+
+		state.TimeoutCts.Cancel();
+		_activeEscalations.TryRemove(state.Request.EscalationId, out _);
+
+		EscalationMetrics.Pending.Add(-1);
+		RecordResolutionMetrics(state, outcome);
+
+		_logger.LogInformation(
+			"Escalation {EscalationId} resolved: {ResolutionType}, approved={IsApproved}",
+			outcome.EscalationId, outcome.ResolutionType, outcome.IsApproved);
+
+		await SafeExecuteAsync(
+			() => _auditStore.RecordOutcomeAsync(outcome, CancellationToken.None),
+			"record outcome", outcome.EscalationId);
+
+		await SafeExecuteAsync(
+			() => _notifier.NotifyEscalationResolvedAsync(outcome, CancellationToken.None),
+			"notify resolution", outcome.EscalationId);
+	}
+
+	private async Task RunTimeoutAsync(EscalationState state)
+	{
+		try
+		{
+			await Task.Delay(
+				TimeSpan.FromSeconds(state.Request.TimeoutSeconds),
+				state.TimeoutCts.Token);
+
+			HandleTimeout(state);
+		}
+		catch (OperationCanceledException)
+		{
+			// Escalation resolved or caller cancelled before timeout -- normal path
+		}
+		catch (Exception ex)
+		{
+			_logger.LogError(ex, "Unexpected error in timeout handler for escalation {EscalationId}",
+				state.Request.EscalationId);
+		}
+	}
+
+	private void HandleTimeout(EscalationState state)
+	{
+		EscalationOutcome? outcome;
+
+		lock (state.Lock)
+		{
+			if (state.IsResolved)
+				return;
+
+			state.IsResolved = true;
+
+			outcome = new EscalationOutcome
+			{
+				EscalationId = state.Request.EscalationId,
+				IsApproved = state.Request.TimeoutAction == EscalationTimeoutAction.Approve,
+				Decisions = state.Decisions.ToList().AsReadOnly(),
+				ResolutionType = EscalationResolutionType.TimedOut,
+				ResolvedAt = DateTimeOffset.UtcNow
+			};
+		}
+
+		EscalationMetrics.Timeouts.Add(1,
+			new KeyValuePair<string, object?>(EscalationConventions.Priority,
+				ToPriorityTag(state.Request.Priority)));
+
+		_logger.LogWarning(
+			"Escalation {EscalationId} timed out with action {TimeoutAction}",
+			state.Request.EscalationId, state.Request.TimeoutAction);
+
+		_ = ResolveEscalationAsync(state, outcome);
+	}
+
+	private void CleanupCancelledEscalation(EscalationState state)
+	{
+		if (_activeEscalations.TryRemove(state.Request.EscalationId, out _))
+		{
+			state.TimeoutCts.Cancel();
+			EscalationMetrics.Pending.Add(-1);
+			_logger.LogWarning("Escalation {EscalationId} cancelled by caller",
+				state.Request.EscalationId);
+		}
+	}
+
+	private static void RecordResolutionMetrics(EscalationState state, EscalationOutcome outcome)
+	{
+		var durationMs = (outcome.ResolvedAt - state.CreatedAt).TotalMilliseconds;
+
+		EscalationMetrics.Resolutions.Add(1,
+			new KeyValuePair<string, object?>(EscalationConventions.ResolutionType,
+				ToResolutionTag(outcome.ResolutionType)),
+			new KeyValuePair<string, object?>(EscalationConventions.Priority,
+				ToPriorityTag(state.Request.Priority)));
+
+		EscalationMetrics.DurationMs.Record(durationMs,
+			new KeyValuePair<string, object?>(EscalationConventions.Priority,
+				ToPriorityTag(state.Request.Priority)));
+	}
+
+	private async Task SafeExecuteAsync(Func<Task> action, string operationName, Guid escalationId)
+	{
+		try
+		{
+			await action();
+		}
+		catch (Exception ex)
+		{
+			_logger.LogError(ex, "Failed to {Operation} for escalation {EscalationId}",
+				operationName, escalationId);
+		}
+	}
+
+	private static string ToPriorityTag(EscalationPriority priority) => priority switch
+	{
+		EscalationPriority.Informational => EscalationConventions.PriorityValues.Informational,
+		EscalationPriority.Blocking => EscalationConventions.PriorityValues.Blocking,
+		EscalationPriority.Critical => EscalationConventions.PriorityValues.Critical,
+		_ => priority.ToString().ToLowerInvariant()
+	};
+
+	private static string ToResolutionTag(EscalationResolutionType resolution) => resolution switch
+	{
+		EscalationResolutionType.Approved => EscalationConventions.ResolutionValues.Approved,
+		EscalationResolutionType.Denied => EscalationConventions.ResolutionValues.Denied,
+		EscalationResolutionType.TimedOut => EscalationConventions.ResolutionValues.TimedOut,
+		EscalationResolutionType.Escalated => EscalationConventions.ResolutionValues.Escalated,
+		_ => resolution.ToString().ToLowerInvariant()
+	};
+
+	private static string ToStrategyTag(ApprovalStrategyType strategy) => strategy switch
+	{
+		ApprovalStrategyType.AnyOf => EscalationConventions.StrategyValues.AnyOf,
+		ApprovalStrategyType.AllOf => EscalationConventions.StrategyValues.AllOf,
+		ApprovalStrategyType.Quorum => EscalationConventions.StrategyValues.Quorum,
+		_ => strategy.ToString().ToLowerInvariant()
+	};
+
+	/// <summary>Tracks the mutable state of an active escalation.</summary>
+	private sealed class EscalationState
+	{
+		public required EscalationRequest Request { get; init; }
+		public List<ApproverDecision> Decisions { get; } = [];
+		public TaskCompletionSource<EscalationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
+		public required CancellationTokenSource TimeoutCts { get; init; }
+		public required DateTimeOffset CreatedAt { get; init; }
+		public bool IsResolved { get; set; }
+		public readonly object Lock = new();
+	}
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs
new file mode 100644
index 0000000..a20a34b
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs
@@ -0,0 +1,368 @@
+using System.Diagnostics;
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Domain.Common.Config.AI.Governance;
+using FluentAssertions;
+using Infrastructure.AI.Escalation;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Escalation;
+
+/// <summary>
+/// Tests for <see cref="DefaultEscalationService"/>.
+/// Verifies escalation lifecycle: creation, strategy evaluation, timeout racing,
+/// cancellation propagation, notification dispatch, and audit recording.
+/// </summary>
+public sealed class DefaultEscalationServiceTests : IDisposable
+{
+	private readonly Mock<IEscalationNotifier> _notifier = new();
+	private readonly Mock<IEscalationAuditStore> _auditStore = new();
+	private readonly Mock<IApprovalStrategy> _anyOfStrategy = new();
+	private readonly Mock<IApprovalStrategy> _allOfStrategy = new();
+	private readonly DefaultEscalationService _sut;
+
+	public DefaultEscalationServiceTests()
+	{
+		_anyOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AnyOf);
+		_allOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AllOf);
+
+		var services = new ServiceCollection();
+		services.AddKeyedSingleton<IApprovalStrategy>(
+			ApprovalStrategyType.AnyOf, (_, _) => _anyOfStrategy.Object);
+		services.AddKeyedSingleton<IApprovalStrategy>(
+			ApprovalStrategyType.AllOf, (_, _) => _allOfStrategy.Object);
+		var serviceProvider = services.BuildServiceProvider();
+
+		var configMonitor = new Mock<IOptionsMonitor<EscalationConfig>>();
+		configMonitor.Setup(m => m.CurrentValue).Returns(new EscalationConfig
+		{
+			Enabled = true,
+			DefaultTimeoutSeconds = 300,
+			DefaultApprovalStrategy = "AnyOf"
+		});
+
+		_sut = new DefaultEscalationService(
+			serviceProvider,
+			_notifier.Object,
+			_auditStore.Object,
+			configMonitor.Object,
+			NullLogger<DefaultEscalationService>.Instance);
+	}
+
+	public void Dispose() => _sut.Dispose();
+
+	// --- Helpers ---
+
+	private static EscalationRequest CreateTestRequest(
+		EscalationPriority priority = EscalationPriority.Blocking,
+		ApprovalStrategyType strategy = ApprovalStrategyType.AnyOf,
+		int timeoutSeconds = 300,
+		EscalationTimeoutAction timeoutAction = EscalationTimeoutAction.DenyAndEscalate,
+		IReadOnlyList<string>? approvers = null) =>
+		new()
+		{
+			EscalationId = Guid.NewGuid(),
+			AgentId = "test-agent",
+			ToolName = "dangerous-tool",
+			Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
+			Description = "Test escalation",
+			RiskLevel = RiskLevel.High,
+			Priority = priority,
+			ApprovalStrategy = strategy,
+			Approvers = approvers ?? ["approver-1", "approver-2"],
+			TimeoutSeconds = timeoutSeconds,
+			TimeoutAction = timeoutAction,
+			RequestedAt = DateTimeOffset.UtcNow
+		};
+
+	private static ApproverDecision CreateApproval(string approverName = "approver-1") =>
+		new()
+		{
+			ApproverName = approverName,
+			Approved = true,
+			Reason = "Looks good",
+			RespondedAt = DateTimeOffset.UtcNow
+		};
+
+	private static ApproverDecision CreateDenial(string approverName = "approver-1") =>
+		new()
+		{
+			ApproverName = approverName,
+			Approved = false,
+			Reason = "Too risky",
+			RespondedAt = DateTimeOffset.UtcNow
+		};
+
+	private void SetupStrategyResolvesOnFirstApproval()
+	{
+		_anyOfStrategy
+			.Setup(s => s.EvaluateDecision(
+				It.IsAny<EscalationRequest>(),
+				It.IsAny<IReadOnlyList<ApproverDecision>>()))
+			.Returns((EscalationRequest _, IReadOnlyList<ApproverDecision> decisions) =>
+				decisions.Any(d => d.Approved)
+					? new ApprovalEvaluation
+					{
+						IsResolved = true,
+						IsApproved = true,
+						PendingApprovers = []
+					}
+					: new ApprovalEvaluation
+					{
+						IsResolved = false,
+						IsApproved = false,
+						PendingApprovers = ["pending"]
+					});
+	}
+
+	private void SetupStrategyNeverResolves(ApprovalStrategyType strategyType)
+	{
+		var mock = strategyType == ApprovalStrategyType.AllOf ? _allOfStrategy : _anyOfStrategy;
+		mock.Setup(s => s.EvaluateDecision(
+				It.IsAny<EscalationRequest>(),
+				It.IsAny<IReadOnlyList<ApproverDecision>>()))
+			.Returns(new ApprovalEvaluation
+			{
+				IsResolved = false,
+				IsApproved = false,
+				PendingApprovers = ["pending"]
+			});
+	}
+
+	// ===== RequestEscalationAsync =====
+
+	[Fact]
+	public async Task RequestEscalationAsync_CreatesEscalation_NotifiesApprovers()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+
+		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
+		await Task.Delay(50);
+
+		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
+		var outcome = await task;
+
+		outcome.IsApproved.Should().BeTrue();
+		_notifier.Verify(
+			n => n.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	[Fact]
+	public async Task RequestEscalationAsync_BlockingMode_AwaitsOutcome()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+
+		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
+
+		var completedEarly = await Task.WhenAny(task, Task.Delay(200)) == task;
+		completedEarly.Should().BeFalse("RequestEscalationAsync should block until resolved");
+
+		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
+		var outcome = await task;
+
+		outcome.Should().NotBeNull();
+		outcome.IsApproved.Should().BeTrue();
+	}
+
+	[Fact]
+	public async Task RequestEscalationAsync_AuditsRequest()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+
+		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
+		await Task.Delay(50);
+
+		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
+		await task;
+
+		_auditStore.Verify(
+			a => a.RecordRequestAsync(request, It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	// ===== QueueEscalationAsync =====
+
+	[Fact]
+	public async Task QueueEscalationAsync_ReturnsEscalationId_DoesNotBlock()
+	{
+		var request = CreateTestRequest();
+
+		var sw = Stopwatch.StartNew();
+		var id = await _sut.QueueEscalationAsync(request, CancellationToken.None);
+		sw.Stop();
+
+		id.Should().Be(request.EscalationId);
+		sw.ElapsedMilliseconds.Should().BeLessThan(1000);
+		_notifier.Verify(
+			n => n.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	// ===== SubmitDecisionAsync =====
+
+	[Fact]
+	public async Task SubmitDecisionAsync_TriggersStrategyEvaluation_ReturnsOutcomeIfResolved()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+		await _sut.QueueEscalationAsync(request, CancellationToken.None);
+
+		var outcome = await _sut.SubmitDecisionAsync(
+			request.EscalationId, CreateApproval(), CancellationToken.None);
+
+		outcome.Should().NotBeNull();
+		outcome!.IsApproved.Should().BeTrue();
+		outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved);
+
+		_auditStore.Verify(
+			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
+			Times.Once);
+		_auditStore.Verify(
+			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
+			Times.Once);
+		_notifier.Verify(
+			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	[Fact]
+	public async Task SubmitDecisionAsync_PartialDecision_ReturnsNull()
+	{
+		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
+		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
+		await _sut.QueueEscalationAsync(request, CancellationToken.None);
+
+		var outcome = await _sut.SubmitDecisionAsync(
+			request.EscalationId, CreateApproval(), CancellationToken.None);
+
+		outcome.Should().BeNull();
+		_auditStore.Verify(
+			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
+			Times.Once);
+		_notifier.Verify(
+			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
+			Times.Never);
+	}
+
+	[Fact]
+	public async Task SubmitDecisionAsync_UnknownEscalationId_ReturnsNull()
+	{
+		var outcome = await _sut.SubmitDecisionAsync(
+			Guid.NewGuid(), CreateApproval(), CancellationToken.None);
+
+		outcome.Should().BeNull();
+		_auditStore.Verify(
+			a => a.RecordDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
+			Times.Never);
+	}
+
+	// ===== Timeout =====
+
+	[Fact]
+	public async Task Timeout_FiresDenyAndEscalate_CompletesWithTimedOut()
+	{
+		var request = CreateTestRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.DenyAndEscalate);
+
+		var outcome = await _sut.RequestEscalationAsync(request, CancellationToken.None);
+
+		outcome.ResolutionType.Should().Be(EscalationResolutionType.TimedOut);
+		outcome.IsApproved.Should().BeFalse();
+		_auditStore.Verify(
+			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	[Fact]
+	public async Task Timeout_CallerCancelled_PropagatesCancellation()
+	{
+		var request = CreateTestRequest(timeoutSeconds: 300);
+		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
+
+		var act = () => _sut.RequestEscalationAsync(request, cts.Token);
+
+		await act.Should().ThrowAsync<OperationCanceledException>();
+
+		var pending = await _sut.GetPendingEscalationAsync(
+			request.EscalationId, CancellationToken.None);
+		pending.Should().BeNull();
+	}
+
+	[Fact]
+	public async Task Timeout_AuditsOutcome()
+	{
+		var request = CreateTestRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.Deny);
+
+		await _sut.RequestEscalationAsync(request, CancellationToken.None);
+
+		_auditStore.Verify(
+			a => a.RecordOutcomeAsync(
+				It.Is<EscalationOutcome>(o => o.ResolutionType == EscalationResolutionType.TimedOut),
+				It.IsAny<CancellationToken>()),
+			Times.Once);
+	}
+
+	// ===== Concurrency =====
+
+	[Fact]
+	public async Task ConcurrentDecisions_ThreadSafe_NoRaceConditions()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+		await _sut.QueueEscalationAsync(request, CancellationToken.None);
+
+		var tasks = Enumerable.Range(1, 3)
+			.Select(i => _sut.SubmitDecisionAsync(
+				request.EscalationId,
+				CreateApproval($"approver-{i}"),
+				CancellationToken.None))
+			.ToArray();
+
+		var outcomes = await Task.WhenAll(tasks);
+		outcomes.Count(o => o is not null).Should().Be(1,
+			"exactly one concurrent decision should resolve the escalation");
+	}
+
+	// ===== GetPending =====
+
+	[Fact]
+	public async Task GetPendingEscalationsAsync_ReturnsOnlyPending()
+	{
+		var req1 = CreateTestRequest();
+		var req2 = CreateTestRequest();
+		var req3 = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+
+		await _sut.QueueEscalationAsync(req1, CancellationToken.None);
+		await _sut.QueueEscalationAsync(req2, CancellationToken.None);
+		await _sut.QueueEscalationAsync(req3, CancellationToken.None);
+
+		await _sut.SubmitDecisionAsync(req1.EscalationId, CreateApproval(), CancellationToken.None);
+
+		var pending = await _sut.GetPendingEscalationsAsync("approver-1", CancellationToken.None);
+		pending.Should().HaveCount(2);
+		pending.Should().NotContain(r => r.EscalationId == req1.EscalationId);
+	}
+
+	[Fact]
+	public async Task GetPendingEscalationAsync_ResolvedEscalation_ReturnsNull()
+	{
+		var request = CreateTestRequest();
+		SetupStrategyResolvesOnFirstApproval();
+		await _sut.QueueEscalationAsync(request, CancellationToken.None);
+
+		await _sut.SubmitDecisionAsync(
+			request.EscalationId, CreateApproval(), CancellationToken.None);
+
+		var pending = await _sut.GetPendingEscalationAsync(
+			request.EscalationId, CancellationToken.None);
+		pending.Should().BeNull();
+	}
+}
