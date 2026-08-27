using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Application.AI.Common.Exceptions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Escalation.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Durability tests for <see cref="DefaultEscalationService"/>: rehydration after a simulated
/// restart (a new service instance over the same SQLite file), decide-after-restart, timeout
/// resumption across downtime, fail-closed behavior when the durable write fails, and the
/// <see cref="IEscalationReconciler"/> recovery path for the audit-outage stuck state —
/// in-memory and post-restart shapes, including idempotency of repeated passes.
/// </summary>
public sealed class DurableEscalationServiceTests : IDisposable
{
	private readonly Mock<IEscalationNotifier> _notifier = new();
	private readonly Mock<IEscalationAuditStore> _auditStore = new();
	private readonly Mock<IApprovalStrategy> _anyOfStrategy = new();
	private readonly IServiceProvider _serviceProvider;
	private readonly string _dbPath;
	private readonly DbContextOptions<GovernanceStateDbContext> _dbOptions;
	private readonly FakeGovernanceRecordSealer _sealer = new();
	private readonly List<DefaultEscalationService> _services = [];

	public DurableEscalationServiceTests()
	{
		_anyOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AnyOf);
		_anyOfStrategy
			.Setup(s => s.EvaluateDecision(
				It.IsAny<EscalationRequest>(),
				It.IsAny<IReadOnlyList<ApproverDecision>>()))
			.Returns((EscalationRequest _, IReadOnlyList<ApproverDecision> decisions) =>
				decisions.Any(d => d.Verdict == ApproverVerdict.Approve)
					? new ApprovalEvaluation { IsResolved = true, Verdict = ApproverVerdict.Approve, PendingApprovers = [] }
					: new ApprovalEvaluation { IsResolved = false, Verdict = ApproverVerdict.Deny, PendingApprovers = ["pending"] });

		var services = new ServiceCollection();
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AnyOf, (_, _) => _anyOfStrategy.Object);
		_serviceProvider = services.BuildServiceProvider();

		_dbPath = Path.Combine(Path.GetTempPath(), $"gov-state-test-{Guid.NewGuid():N}.db");
		_dbOptions = new DbContextOptionsBuilder<GovernanceStateDbContext>()
			.UseSqlite($"DataSource={_dbPath}")
			.Options;
	}

	public void Dispose()
	{
		foreach (var service in _services)
			service.Dispose();
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
			// Best-effort temp cleanup; the OS temp folder reaps leftovers.
		}
	}

	// --- Helpers ---

	private DefaultEscalationService CreateService(IEscalationStateStore stateStore)
	{
		var configMonitor = new Mock<IOptionsMonitor<EscalationConfig>>();
		configMonitor.Setup(m => m.CurrentValue).Returns(new EscalationConfig
		{
			Enabled = true,
			DefaultTimeoutSeconds = 300,
			DefaultApprovalStrategy = "AnyOf"
		});

		var service = new DefaultEscalationService(
			_serviceProvider,
			_notifier.Object,
			_auditStore.Object,
			stateStore,
			configMonitor.Object,
			NullLogger<DefaultEscalationService>.Instance);
		_services.Add(service);
		return service;
	}

	private EfCoreEscalationStateStore CreateDurableStore()
	{
		var factory = new TestContextFactory(_dbOptions);
		return new EfCoreEscalationStateStore(
			factory,
			new SchemaInitializer<GovernanceStateDbContext>(factory),
			_sealer,
			GovernanceStateTestConfig.Monitor(),
			NullLogger<EfCoreEscalationStateStore>.Instance);
	}

	private static EscalationRequest CreateRequest(int timeoutSeconds = 300) => new()
	{
		EscalationId = Guid.NewGuid(),
		AgentId = "agent-001",
		ToolName = "dangerous_tool",
		Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
		Description = "Test escalation",
		RiskLevel = RiskLevel.High,
		Priority = EscalationPriority.Blocking,
		ApprovalStrategy = ApprovalStrategyType.AnyOf,
		Approvers = ["approver-1", "approver-2"],
		TimeoutSeconds = timeoutSeconds,
		TimeoutAction = EscalationTimeoutAction.DenyAndEscalate,
		RequestedAt = DateTimeOffset.UtcNow
	};

	private static ApproverDecision CreateApproval(string approverName = "approver-1") => new()
	{
		ApproverName = approverName,
		Verdict = ApproverVerdict.Approve,
		Reason = "Looks good",
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

	private static async Task<EscalationOutcome> PollOutcomeAsync(
		DefaultEscalationService service, Guid escalationId)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (true)
		{
			var outcome = await service.GetOutcomeAsync(escalationId, CancellationToken.None);
			if (outcome is not null)
				return outcome;
			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(20, cts.Token);
		}
	}

	// ===== Rehydration after restart =====

	[Fact]
	public async Task RehydratePendingEscalationsAsync_AfterRestart_RestoresPendingEscalation()
	{
		var request = CreateRequest();
		var first = CreateService(CreateDurableStore());
		await first.QueueEscalationAsync(request, CancellationToken.None);
		first.Dispose();

		// Simulated restart: fresh service + fresh store over the same database file.
		var rebooted = CreateService(CreateDurableStore());
		var restored = await rebooted.RehydratePendingEscalationsAsync(CancellationToken.None);

		restored.Should().Be(1);
		(await rebooted.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().BeEquivalentTo(request);
		(await rebooted.GetPendingEscalationsAsync("approver-1", CancellationToken.None))
			.Should().ContainSingle().Which.EscalationId.Should().Be(request.EscalationId);
	}

	[Fact]
	public async Task SubmitDecisionAsync_AfterRestart_ResolvesAndOutcomeSurvivesSecondRestart()
	{
		var request = CreateRequest();
		var first = CreateService(CreateDurableStore());
		await first.QueueEscalationAsync(request, CancellationToken.None);
		first.Dispose();

		var rebooted = CreateService(CreateDurableStore());
		await rebooted.RehydratePendingEscalationsAsync(CancellationToken.None);
		var result = await rebooted.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.Resolved);

		// Second restart: the audited outcome must be durably queryable with no rehydration.
		var third = CreateService(CreateDurableStore());
		var outcome = await third.GetOutcomeAsync(request.EscalationId, CancellationToken.None);
		outcome!.IsApproved.Should().BeTrue();
		outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved);
	}

	[Fact]
	public async Task RehydratePendingEscalationsAsync_TimeoutExpiredDuringDowntime_TimesOutImmediately()
	{
		var request = CreateRequest(timeoutSeconds: 1);

		// A row left Pending by a host that died before its timeout could fire, created far enough
		// back that its deadline has already passed. Seeded straight into the store rather than
		// queued through a service and disposed (#537): that sequence armed a live one-second timer
		// and then raced Dispose against it. Under suite load the few statements between the two
		// took longer than the second, so the escalation timed out in-process, left nothing Pending,
		// and rehydration restored 0. Nothing here reads the wall clock, so there is no race left
		// to lose — and the queue-then-restart path this replaced is covered whole by
		// RehydratePendingEscalationsAsync_AfterRestart_RestoresPendingEscalation above.
		await CreateDurableStore().SavePendingAsync(
			request, DateTimeOffset.UtcNow.AddSeconds(-10), CancellationToken.None);

		var rebooted = CreateService(CreateDurableStore());
		var restored = await rebooted.RehydratePendingEscalationsAsync(CancellationToken.None);
		restored.Should().Be(1);

		// Downtime counted against the timeout budget: the rehydrated escalation expires
		// immediately via its configured timeout action instead of restarting the clock.
		var outcome = await PollOutcomeAsync(rebooted, request.EscalationId);
		outcome.ResolutionType.Should().Be(EscalationResolutionType.TimedOut);
		outcome.IsApproved.Should().BeFalse();
	}

	// ===== Fail-closed durable writes =====

	[Fact]
	public async Task QueueEscalationAsync_DurableCreateFails_EscalationNotOpened()
	{
		var store = new TogglableStateStore { FailPending = true };
		var service = CreateService(store);
		var request = CreateRequest();

		var act = () => service.QueueEscalationAsync(request, CancellationToken.None);

		// Scrubbed at the service boundary: the raw provider exception never escapes toward a
		// transport, only a stable code, with the original preserved for structured logging.
		var thrown = await act.Should().ThrowAsync<EscalationDurableStateException>();
		thrown.Which.Code.Should().Be(EscalationDurableStateException.DurableCreateFailedCode);
		thrown.Which.Message.Should().NotContain("durable store unavailable");
		thrown.Which.InnerException.Should().BeOfType<IOException>();

		(await service.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull();
	}

	[Fact]
	public async Task SubmitDecisionAsync_DurableDecisionWriteFails_FailsClosedAndRetrySucceeds()
	{
		var store = new TogglableStateStore();
		var service = CreateService(store);
		var request = CreateRequest();
		await service.QueueEscalationAsync(request, CancellationToken.None);

		store.FailDecisions = true;
		var act = () => service.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		var thrown = await act.Should().ThrowAsync<EscalationDurableStateException>();
		thrown.Which.Code.Should().Be(EscalationDurableStateException.DurableWriteFailedCode);
		thrown.Which.Message.Should().NotContain("durable store unavailable");

		// Fail-closed: still pending, and the ghost decision was backed out — the SAME
		// approver's retry must not be rejected as a duplicate once the store recovers.
		(await service.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().NotBeNull();

		store.FailDecisions = false;
		var result = await service.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);
		result.Status.Should().Be(EscalationDecisionStatus.Resolved);
	}

	// ===== Reconcile: the audit-outage recovery path =====

	[Fact]
	public async Task ReconcileStuckEscalationsAsync_AuditOutageDuringDecide_RecoversOnceHealed()
	{
		var service = CreateService(new NullEscalationStateStore());
		var request = CreateRequest();
		await service.QueueEscalationAsync(request, CancellationToken.None);

		FailOutcomeAudit();
		var act = () => service.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		await act.Should().ThrowAsync<IOException>();

		// Fail-closed: not reported resolved, still observable, and no outcome served.
		(await service.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().NotBeNull();
		(await service.GetOutcomeAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull();

		HealOutcomeAudit();
		var reconcile = await service.ReconcileStuckEscalationsAsync(CancellationToken.None);

		reconcile.Recovered.Should().ContainSingle().Which.Should().Be(request.EscalationId);
		reconcile.StillStuck.Should().BeEmpty();
		(await service.GetOutcomeAsync(request.EscalationId, CancellationToken.None))!
			.IsApproved.Should().BeTrue();
		(await service.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull();

		// Idempotency: a second pass over a healthy system recovers nothing.
		var second = await service.ReconcileStuckEscalationsAsync(CancellationToken.None);
		second.Recovered.Should().BeEmpty();
		second.StillStuck.Should().BeEmpty();
	}

	[Fact]
	public async Task ReconcileStuckEscalationsAsync_AuditStillDown_ReportsStillStuck()
	{
		var service = CreateService(new NullEscalationStateStore());
		var request = CreateRequest();
		await service.QueueEscalationAsync(request, CancellationToken.None);

		FailOutcomeAudit();
		var act = () => service.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		await act.Should().ThrowAsync<IOException>();

		var reconcile = await service.ReconcileStuckEscalationsAsync(CancellationToken.None);

		reconcile.Recovered.Should().BeEmpty();
		reconcile.StillStuck.Should().ContainSingle().Which.Should().Be(request.EscalationId);
		(await service.GetOutcomeAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull();

		// The stuck state stays claimable: a later pass with a healed audit store recovers it.
		HealOutcomeAudit();
		var healed = await service.ReconcileStuckEscalationsAsync(CancellationToken.None);
		healed.Recovered.Should().ContainSingle().Which.Should().Be(request.EscalationId);
	}

	[Fact]
	public async Task ReconcileStuckEscalationsAsync_AfterRestart_FinalizesDurableStuckRecord()
	{
		var request = CreateRequest();
		var first = CreateService(CreateDurableStore());
		await first.QueueEscalationAsync(request, CancellationToken.None);

		FailOutcomeAudit();
		var act = () => first.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		await act.Should().ThrowAsync<IOException>();
		first.Dispose();

		// Restart: the record is ResolvedPendingAudit, so it must NOT rehydrate as pending —
		// its resolution already happened; only the audit write is owed.
		var rebooted = CreateService(CreateDurableStore());
		(await rebooted.RehydratePendingEscalationsAsync(CancellationToken.None)).Should().Be(0);

		HealOutcomeAudit();
		var reconcile = await rebooted.ReconcileStuckEscalationsAsync(CancellationToken.None);

		reconcile.Recovered.Should().ContainSingle().Which.Should().Be(request.EscalationId);
		var outcome = await rebooted.GetOutcomeAsync(request.EscalationId, CancellationToken.None);
		outcome!.IsApproved.Should().BeTrue();

		// A third restart still serves the audited outcome from the durable store.
		var third = CreateService(CreateDurableStore());
		(await third.GetOutcomeAsync(request.EscalationId, CancellationToken.None))!
			.IsApproved.Should().BeTrue();
	}

	// ===== Flag-off parity =====

	[Fact]
	public async Task GetOutcomeAsync_NullStore_UnknownEscalation_ReturnsNull()
	{
		var service = CreateService(new NullEscalationStateStore());

		var outcome = await service.GetOutcomeAsync(Guid.NewGuid(), CancellationToken.None);

		outcome.Should().BeNull();
	}

	/// <summary>
	/// Delegates to <see cref="NullEscalationStateStore"/> with per-operation failure toggles,
	/// for exercising the fail-closed durable-write paths deterministically.
	/// </summary>
	private sealed class TogglableStateStore : IEscalationStateStore
	{
		private readonly NullEscalationStateStore _inner = new();

		public bool FailPending { get; set; }
		public bool FailDecisions { get; set; }

		public Task SavePendingAsync(EscalationRequest request, DateTimeOffset createdAt, CancellationToken ct)
			=> FailPending
				? throw new IOException("durable store unavailable")
				: _inner.SavePendingAsync(request, createdAt, ct);

		public Task SaveDecisionsAsync(Guid escalationId, IReadOnlyList<ApproverDecision> decisions, CancellationToken ct)
			=> FailDecisions
				? throw new IOException("durable store unavailable")
				: _inner.SaveDecisionsAsync(escalationId, decisions, ct);

		public Task MarkResolvedPendingAuditAsync(EscalationOutcome outcome, CancellationToken ct)
			=> _inner.MarkResolvedPendingAuditAsync(outcome, ct);

		public Task MarkResolvedAsync(Guid escalationId, CancellationToken ct)
			=> _inner.MarkResolvedAsync(escalationId, ct);

		public Task RemoveAsync(Guid escalationId, CancellationToken ct)
			=> _inner.RemoveAsync(escalationId, ct);

		public Task<bool> TryClaimResolvedPendingAuditAsync(
			Guid escalationId, DateTimeOffset staleClaimBefore, CancellationToken ct)
			=> _inner.TryClaimResolvedPendingAuditAsync(escalationId, staleClaimBefore, ct);

		public Task ReleaseClaimAsync(Guid escalationId, CancellationToken ct)
			=> _inner.ReleaseClaimAsync(escalationId, ct);

		public Task<IReadOnlyList<EscalationStateSnapshot>> GetActiveAsync(CancellationToken ct)
			=> _inner.GetActiveAsync(ct);

		public Task<EscalationOutcome?> GetResolvedOutcomeAsync(Guid escalationId, CancellationToken ct)
			=> _inner.GetResolvedOutcomeAsync(escalationId, ct);
	}

	/// <summary>
	/// Minimal <see cref="IDbContextFactory{TContext}"/> over fixed options, mirroring the
	/// planner store tests' factory double.
	/// </summary>
	private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
		: IDbContextFactory<GovernanceStateDbContext>
	{
		public GovernanceStateDbContext CreateDbContext() => new(options);
	}
}
