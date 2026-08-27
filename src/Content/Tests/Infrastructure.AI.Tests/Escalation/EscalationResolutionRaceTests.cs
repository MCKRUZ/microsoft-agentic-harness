using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Correctness-review regressions around escalation resolution: the audit-outage parked state
/// must be recovered on hosts with durable governance state switched OFF, a cancellation racing
/// a failing decision write must not strand a durable record, and a teardown racing an in-flight
/// decision must not surface an <see cref="ObjectDisposedException"/> to the approver.
/// </summary>
/// <remarks>
/// All three shapes are reachable in the DEFAULT configuration. The parked state is produced by
/// the <em>audit</em> store failing, which is independent of the durability toggles, so none of
/// this is exotic-config-only behaviour.
/// </remarks>
public sealed class EscalationResolutionRaceTests : IDisposable
{
	private readonly Mock<IEscalationNotifier> _notifier = new();
	private readonly Mock<IEscalationAuditStore> _auditStore = new();
	private readonly Mock<IApprovalStrategy> _anyOfStrategy = new();
	private readonly Mock<IApprovalStrategy> _allOfStrategy = new();
	private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
	private readonly List<DefaultEscalationService> _services = [];

	public EscalationResolutionRaceTests()
	{
		// AnyOf: the first approval resolves.
		_anyOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AnyOf);
		_anyOfStrategy
			.Setup(s => s.EvaluateDecision(
				It.IsAny<EscalationRequest>(), It.IsAny<IReadOnlyList<ApproverDecision>>()))
			.Returns((EscalationRequest _, IReadOnlyList<ApproverDecision> decisions) =>
				decisions.Any(d => d.Verdict == ApproverVerdict.Approve)
					? new ApprovalEvaluation { IsResolved = true, Verdict = ApproverVerdict.Approve, PendingApprovers = [] }
					: new ApprovalEvaluation { IsResolved = false, Verdict = ApproverVerdict.Deny, PendingApprovers = ["pending"] });

		// AllOf: a single approval on a two-approver roster does NOT resolve. Required by the
		// cancel-race test — a resolving decision would flip IsResolved inside the state lock
		// before its durable write even starts, so the cancellation could never reach the
		// interleaving under test (it would simply see an already-resolved escalation).
		_allOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AllOf);
		_allOfStrategy
			.Setup(s => s.EvaluateDecision(
				It.IsAny<EscalationRequest>(), It.IsAny<IReadOnlyList<ApproverDecision>>()))
			.Returns((EscalationRequest request, IReadOnlyList<ApproverDecision> decisions) =>
				new ApprovalEvaluation
				{
					IsResolved = decisions.Count >= request.Approvers.Count && decisions.All(d => d.Verdict == ApproverVerdict.Approve),
					Verdict = decisions.Count >= request.Approvers.Count && decisions.All(d => d.Verdict == ApproverVerdict.Approve)
						? ApproverVerdict.Approve
						: ApproverVerdict.Deny,
					PendingApprovers = []
				});
	}

	public void Dispose()
	{
		foreach (var service in _services)
			service.Dispose();
	}

	// --- Helpers ---

	private DefaultEscalationService CreateService(
		IEscalationStateStore stateStore, IServiceProvider? serviceProvider = null)
	{
		var configMonitor = new Mock<IOptionsMonitor<EscalationConfig>>();
		configMonitor.Setup(m => m.CurrentValue).Returns(new EscalationConfig { Enabled = true });

		var service = new DefaultEscalationService(
			serviceProvider ?? BuildStrategyProvider(),
			_notifier.Object,
			_auditStore.Object,
			stateStore,
			configMonitor.Object,
			NullLogger<DefaultEscalationService>.Instance);
		_services.Add(service);
		return service;
	}

	private IServiceProvider BuildStrategyProvider()
	{
		var services = new ServiceCollection();
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AnyOf, (_, _) => _anyOfStrategy.Object);
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AllOf, (_, _) => _allOfStrategy.Object);
		return services.BuildServiceProvider();
	}

	/// <summary>
	/// Builds the hosted reconciliation service over a real reconciler. The pruner factory throws
	/// on resolution: with both durability toggles off it must never be reached, because
	/// constructing the pruner creates the governance-state database file.
	/// </summary>
	private EscalationReconciliationService CreateReconciliationService(
		IEscalationReconciler reconciler, bool escalationsEnabled, bool changeProposalsEnabled)
	{
		var config = new AppConfig();
		config.AI.Governance.DurableState.EscalationsEnabled = escalationsEnabled;
		config.AI.Governance.DurableState.ChangeProposalsEnabled = changeProposalsEnabled;

		var monitor = new Mock<IOptionsMonitor<AppConfig>>();
		monitor.Setup(m => m.CurrentValue).Returns(config);

		return new EscalationReconciliationService(
			reconciler,
			() => throw new InvalidOperationException(
				"The retention pruner must stay unresolved while both durability toggles are off."),
			monitor.Object,
			_time,
			NullLogger<EscalationReconciliationService>.Instance);
	}

	private static EscalationRequest CreateRequest(
		ApprovalStrategyType strategy = ApprovalStrategyType.AnyOf) => new()
		{
			EscalationId = Guid.NewGuid(),
			AgentId = "agent-001",
			ToolName = "dangerous_tool",
			Arguments = new Dictionary<string, string>(),
			Description = "Test escalation",
			RiskLevel = RiskLevel.High,
			Priority = EscalationPriority.Blocking,
			ApprovalStrategy = strategy,
			Approvers = ["approver-1", "approver-2"],
			TimeoutSeconds = 300,
			TimeoutAction = EscalationTimeoutAction.DenyAndEscalate,
			RequestedAt = DateTimeOffset.UtcNow
		};

	private static ApproverDecision Approval(string approverName) => new()
	{
		ApproverName = approverName,
		Verdict = ApproverVerdict.Approve,
		RespondedAt = DateTimeOffset.UtcNow
	};

	private void FailOutcomeAudit() =>
		_auditStore
			.Setup(a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new IOException("audit store unavailable"));

	private void HealOutcomeAudit() =>
		_auditStore
			.Setup(a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

	/// <summary>
	/// Polls for a verdict while nudging the fake clock, so the hosted service's initial delay and
	/// interval timers actually fire. Advancing on every poll avoids the scheduling race where a
	/// single up-front advance lands before the timer is registered.
	/// </summary>
	private async Task<EscalationOutcome> PollOutcomeDrivingClockAsync(
		DefaultEscalationService service, Guid escalationId)
	{
		using var cts = new CancellationTokenSource(EscalationTestDeadlines.BackgroundWork);
		while (true)
		{
			var outcome = await service.GetOutcomeAsync(escalationId, CancellationToken.None);
			if (outcome is not null)
				return outcome;
			cts.Token.ThrowIfCancellationRequested();
			_time.Advance(TimeSpan.FromSeconds(31));
			await Task.Delay(10, cts.Token);
		}
	}

	// ===== Finding 2: recovery must run with durability OFF =====

	[Fact]
	public async Task ReconciliationService_BothToggglesOff_StillRecoversAuditOutageParkedEscalation()
	{
		// The default configuration. The escalation parks because the fail-closed AUDIT write
		// throws — nothing to do with durable state — so gating the whole reconciliation loop on
		// the durability toggles left this with no scheduled recovery at all, and made
		// AwaitingReconciliation's own contract ("poll until reconciliation completes") false.
		var service = CreateService(new NullEscalationStateStore());
		var request = CreateRequest();
		await service.QueueEscalationAsync(request, CancellationToken.None);

		FailOutcomeAudit();
		await Assert.ThrowsAsync<IOException>(() => service.SubmitDecisionAsync(
			request.EscalationId, Approval("approver-1"), CancellationToken.None));

		// Parked: no verdict is observable, and a second approver is told plainly that their
		// vote did not participate.
		(await service.GetOutcomeAsync(request.EscalationId, CancellationToken.None)).Should().BeNull();
		var late = await service.SubmitDecisionAsync(
			request.EscalationId, Approval("approver-2"), CancellationToken.None);
		late.Status.Should().Be(EscalationDecisionStatus.AwaitingReconciliation);

		HealOutcomeAudit();

		var hosted = CreateReconciliationService(
			service, escalationsEnabled: false, changeProposalsEnabled: false);
		await hosted.StartAsync(CancellationToken.None);
		try
		{
			var outcome = await PollOutcomeDrivingClockAsync(service, request.EscalationId);

			outcome.IsApproved.Should().BeTrue(
				"the scheduled reconcile must re-drive the parked verdict even with durable " +
				"governance state switched off — the parked state is audit-store-induced");
			(await service.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
				.Should().BeNull("a recovered escalation leaves the active set");
		}
		finally
		{
			await hosted.StopAsync(CancellationToken.None);
		}
	}

	// ===== Finding 4: a cancel racing a failed decision write must not strand a durable row =====

	[Fact]
	public async Task CancelEscalationAsync_RacingFailedDecisionWrite_DoesNotStrandDurableRecord()
	{
		var store = new RecordingStateStore();
		var service = CreateService(store);

		// AllOf on a two-approver roster: the single approval below does NOT resolve, so the
		// escalation is still genuinely open when the cancellation arrives. That is what makes
		// the interleaving reachable at all.
		var request = CreateRequest(ApprovalStrategyType.AllOf);
		await service.QueueEscalationAsync(request, CancellationToken.None);

		// A decision that will fail its fail-closed durable write, parked mid-write so the
		// cancellation below has a window to interleave.
		store.BlockAndFailDecisionWrite = true;
		var decisionTask = Task.Run(() => service.SubmitDecisionAsync(
			request.EscalationId, Approval("approver-1"), CancellationToken.None));
		await store.DecisionWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

		// The audit store is down, so the cancellation will park after writing its durable
		// resolution marker — the exact state the decision's rollback used to erase.
		FailOutcomeAudit();
		var cancelTask = Task.Run(() => service.CancelEscalationAsync(
			request.EscalationId, "superseded", "admin", CancellationToken.None));

		// The heart of the fix: cancellation resolves the escalation, so it must serialize on the
		// same per-escalation write gate the decision holds. Before the fix it ran straight
		// through and wrote the durable ResolvedPendingAudit row while the decision was still
		// parked; the decision's rollback then cleared IsResolved/PendingOutcome, leaving that
		// row unclaimable by either reconcile shape.
		var raced = await Task.WhenAny(
			store.ResolvedPendingAuditWritten.Task, Task.Delay(TimeSpan.FromSeconds(2)));
		raced.Should().NotBeSameAs(store.ResolvedPendingAuditWritten.Task,
			"a cancellation must not reach the durable resolution write while a decision write holds the gate");

		store.ReleaseDecisionWrite.TrySetResult();
		await Assert.ThrowsAsync<EscalationDurableStateException>(() => decisionTask);
		await Assert.ThrowsAsync<IOException>(() => cancelTask);

		// The invariant: whatever the interleaving, the parked resolution stays claimable, so no
		// durable row is left behind that reconciliation cannot finish and the pruner (which
		// correctly refuses to delete non-terminal rows) can never clean up.
		HealOutcomeAudit();
		var reconcile = await service.ReconcileStuckEscalationsAsync(CancellationToken.None);

		reconcile.Recovered.Should().ContainSingle().Which.Should().Be(request.EscalationId);
		store.StatusOf(request.EscalationId).Should().Be(EscalationPersistedStatus.Resolved);
		store.NonTerminalIds.Should().BeEmpty(
			"a cancel racing a failed decision write must not strand a durable row");
	}

	// ===== Finding 3: a teardown racing an in-flight decision must not throw ObjectDisposedException =====

	[Fact]
	public async Task SubmitDecisionAsync_WriteGateDisposedByConcurrentTeardown_ReportsUnknownEscalation()
	{
		// CleanupCancelledEscalation disposes the write gate when the blocking caller abandons
		// the escalation. A decision already past the active-set lookup then hits a disposed
		// semaphore: unguarded, that ObjectDisposedException escapes SubmitDecisionAsync and
		// reaches the approver as a 500 instead of the honest "this escalation no longer exists".
		var strategyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStrategy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var services = new ServiceCollection();
		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AnyOf, (_, _) =>
		{
			// Parks the decision path after the active-set lookup but before the gate acquire —
			// precisely the window the teardown races.
			strategyRequested.TrySetResult();
			releaseStrategy.Task.GetAwaiter().GetResult();
			return _anyOfStrategy.Object;
		});

		var service = CreateService(new NullEscalationStateStore(), services.BuildServiceProvider());
		var request = CreateRequest();

		using var callerCts = new CancellationTokenSource();
		var blockingCaller = Task.Run(() => service.RequestEscalationAsync(request, callerCts.Token));

		await WaitForPendingAsync(service, request.EscalationId);

		var decisionTask = Task.Run(() => service.SubmitDecisionAsync(
			request.EscalationId, Approval("approver-1"), CancellationToken.None));
		await strategyRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

		// The caller abandons the escalation: cleanup removes it from the active set and disposes
		// its synchronization primitives, including the gate the parked decision is about to take.
		await callerCts.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockingCaller);

		releaseStrategy.TrySetResult();
		var result = await decisionTask;

		result.Status.Should().Be(EscalationDecisionStatus.UnknownEscalation,
			"a torn-down escalation is not decidable, and that is exactly what a lookup a moment " +
			"later would report — the approver must never see a disposal fault");
	}

	private static async Task WaitForPendingAsync(DefaultEscalationService service, Guid escalationId)
	{
		using var cts = new CancellationTokenSource(EscalationTestDeadlines.BackgroundWork);
		while (await service.GetPendingEscalationAsync(escalationId, CancellationToken.None) is null)
		{
			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(10, cts.Token);
		}
	}

	/// <summary>
	/// In-memory <see cref="IEscalationStateStore"/> that records the persisted status of every
	/// record and can park-then-fail the decision write on demand, so a resolution can be forced
	/// to interleave with a decision deterministically.
	/// </summary>
	private sealed class RecordingStateStore : IEscalationStateStore
	{
		private readonly Dictionary<Guid, Row> _rows = [];
		private readonly object _sync = new();

		/// <summary>Signalled once the (blocking) decision write has been entered.</summary>
		public TaskCompletionSource DecisionWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completed by the test to let the blocked decision write fail.</summary>
		public TaskCompletionSource ReleaseDecisionWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Signalled the first time a durable resolution marker is written.</summary>
		public TaskCompletionSource ResolvedPendingAuditWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>When set, the next decision write parks and then throws.</summary>
		public bool BlockAndFailDecisionWrite { get; set; }

		/// <summary>Ids of records that are not terminal — a stranded row shows up here.</summary>
		public IReadOnlyList<Guid> NonTerminalIds
		{
			get
			{
				lock (_sync)
				{
					return _rows.Where(r => r.Value.Status != EscalationPersistedStatus.Resolved)
						.Select(r => r.Key).ToList();
				}
			}
		}

		/// <summary>Returns the persisted status of a record, or null when absent.</summary>
		/// <param name="escalationId">The record to inspect.</param>
		public EscalationPersistedStatus? StatusOf(Guid escalationId)
		{
			lock (_sync)
			{
				return _rows.TryGetValue(escalationId, out var row) ? row.Status : null;
			}
		}

		/// <inheritdoc />
		public Task SavePendingAsync(EscalationRequest request, DateTimeOffset createdAt, CancellationToken ct)
		{
			lock (_sync)
			{
				_rows[request.EscalationId] = new Row(request, createdAt);
			}
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task SaveDecisionsAsync(
			Guid escalationId, IReadOnlyList<ApproverDecision> decisions, CancellationToken ct)
		{
			if (BlockAndFailDecisionWrite)
				return ParkThenFailAsync();

			lock (_sync)
			{
				if (_rows.TryGetValue(escalationId, out var row))
					row.Decisions = decisions;
			}
			return Task.CompletedTask;

			async Task ParkThenFailAsync()
			{
				DecisionWriteEntered.TrySetResult();
				await ReleaseDecisionWrite.Task;
				throw new IOException("durable store unavailable");
			}
		}

		/// <inheritdoc />
		public Task MarkResolvedPendingAuditAsync(EscalationOutcome outcome, CancellationToken ct)
		{
			lock (_sync)
			{
				if (_rows.TryGetValue(outcome.EscalationId, out var row))
				{
					row.Status = EscalationPersistedStatus.ResolvedPendingAudit;
					row.Outcome = outcome;
				}
			}
			ResolvedPendingAuditWritten.TrySetResult();
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task MarkResolvedAsync(Guid escalationId, CancellationToken ct)
		{
			lock (_sync)
			{
				if (_rows.TryGetValue(escalationId, out var row))
					row.Status = EscalationPersistedStatus.Resolved;
			}
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task RemoveAsync(Guid escalationId, CancellationToken ct)
		{
			lock (_sync)
			{
				_rows.Remove(escalationId);
			}
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task<bool> TryClaimResolvedPendingAuditAsync(
			Guid escalationId, DateTimeOffset staleClaimBefore, CancellationToken ct)
		{
			lock (_sync)
			{
				if (!_rows.TryGetValue(escalationId, out var row) ||
					row.Status is not (EscalationPersistedStatus.ResolvedPendingAudit
						or EscalationPersistedStatus.AuditInFlight))
				{
					return Task.FromResult(false);
				}

				row.Status = EscalationPersistedStatus.AuditInFlight;
				return Task.FromResult(true);
			}
		}

		/// <inheritdoc />
		public Task ReleaseClaimAsync(Guid escalationId, CancellationToken ct)
		{
			lock (_sync)
			{
				if (_rows.TryGetValue(escalationId, out var row) &&
					row.Status == EscalationPersistedStatus.AuditInFlight)
				{
					row.Status = EscalationPersistedStatus.ResolvedPendingAudit;
				}
			}
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task<IReadOnlyList<EscalationStateSnapshot>> GetActiveAsync(CancellationToken ct)
		{
			lock (_sync)
			{
				IReadOnlyList<EscalationStateSnapshot> active = _rows.Values
					.Where(r => r.Status != EscalationPersistedStatus.Resolved)
					.Select(r => new EscalationStateSnapshot
					{
						Request = r.Request,
						Decisions = r.Decisions,
						CreatedAt = r.CreatedAt,
						Status = r.Status,
						Outcome = r.Outcome
					})
					.ToList();
				return Task.FromResult(active);
			}
		}

		/// <inheritdoc />
		public Task<EscalationOutcome?> GetResolvedOutcomeAsync(Guid escalationId, CancellationToken ct)
		{
			lock (_sync)
			{
				return Task.FromResult(
					_rows.TryGetValue(escalationId, out var row) &&
					row.Status == EscalationPersistedStatus.Resolved
						? row.Outcome
						: null);
			}
		}

		private sealed class Row(EscalationRequest request, DateTimeOffset createdAt)
		{
			public EscalationRequest Request { get; } = request;
			public DateTimeOffset CreatedAt { get; } = createdAt;
			public IReadOnlyList<ApproverDecision> Decisions { get; set; } = [];
			public EscalationPersistedStatus Status { get; set; } = EscalationPersistedStatus.Pending;
			public EscalationOutcome? Outcome { get; set; }
		}
	}
}
