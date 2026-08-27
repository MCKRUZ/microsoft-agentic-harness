using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Escalation.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Regression tests for the hardening review findings: a poisoned row must not stop host
/// startup, an invariant-violating row must not rehydrate as a live escalation, reconciliation
/// must actually run in a hosted context, concurrent approvals must not lose a decision, and a
/// tampered outcome must never be re-driven into the compliance audit log.
/// </summary>
public sealed class DurableEscalationHardeningTests : IDisposable
{
	private readonly Mock<IEscalationNotifier> _notifier = new();
	private readonly Mock<IEscalationAuditStore> _auditStore = new();
	private readonly Mock<IApprovalStrategy> _allOfStrategy = new();
	private readonly IServiceProvider _serviceProvider;
	private readonly FakeGovernanceRecordSealer _sealer = new();
	private readonly string _dbPath;
	private readonly DbContextOptions<GovernanceStateDbContext> _dbOptions;
	private readonly List<DefaultEscalationService> _services = [];

	public DurableEscalationHardeningTests()
	{
		_allOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AllOf);

		// AllOf: resolves only once every roster member has approved. This is the strategy that
		// exposes the lost-decision race — non-resolving approvals accumulate.
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

		var services = new ServiceCollection();
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AllOf, (_, _) => _allOfStrategy.Object);
		_serviceProvider = services.BuildServiceProvider();

		_dbPath = Path.Combine(Path.GetTempPath(), $"gov-harden-{Guid.NewGuid():N}.db");
		_dbOptions = new DbContextOptionsBuilder<GovernanceStateDbContext>()
			.UseSqlite($"DataSource={_dbPath}")
			.Options;
	}

	public void Dispose()
	{
		foreach (var service in _services)
			service.Dispose();
		SqliteConnection.ClearAllPools();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
			// Best-effort temp cleanup.
		}
	}

	// --- Helpers ---

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

	private DefaultEscalationService CreateService(IEscalationStateStore stateStore)
	{
		var configMonitor = new Mock<IOptionsMonitor<Domain.Common.Config.AI.Governance.EscalationConfig>>();
		configMonitor.Setup(m => m.CurrentValue)
			.Returns(new Domain.Common.Config.AI.Governance.EscalationConfig { Enabled = true });

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

	private static EscalationRequest CreateRequest(
		IReadOnlyList<string>? approvers = null,
		int timeoutSeconds = 300,
		EscalationTimeoutAction timeoutAction = EscalationTimeoutAction.DenyAndEscalate) => new()
		{
			EscalationId = Guid.NewGuid(),
			AgentId = "agent-001",
			ToolName = "dangerous_tool",
			Arguments = new Dictionary<string, string>(),
			Description = "Test escalation",
			RiskLevel = RiskLevel.High,
			Priority = EscalationPriority.Blocking,
			ApprovalStrategy = ApprovalStrategyType.AllOf,
			Approvers = approvers ?? ["approver-1", "approver-2"],
			TimeoutSeconds = timeoutSeconds,
			TimeoutAction = timeoutAction,
			RequestedAt = DateTimeOffset.UtcNow
		};

	private static ApproverDecision Approval(string approverName) => new()
	{
		ApproverName = approverName,
		Verdict = ApproverVerdict.Approve,
		RespondedAt = DateTimeOffset.UtcNow
	};

	private async Task InsertRawRowAsync(
		Guid id, string status, string requestJson, long createdAtTicks, string decisionsJson = "[]")
	{
		await using var context = new GovernanceStateDbContext(_dbOptions);
		await context.Database.EnsureCreatedAsync();
		await context.Database.ExecuteSqlRawAsync(
			"INSERT INTO escalation_state (Id, Status, RequestJson, DecisionsJson, OutcomeJson, OutcomeSealJson, CreatedAtTicks, UpdatedAtTicks) " +
			"VALUES ({0}, {1}, {2}, {3}, NULL, NULL, {4}, {5});",
			id, status, requestJson, decisionsJson, createdAtTicks, DateTimeOffset.UtcNow.UtcTicks);
	}

	private static string SerializeRequest(EscalationRequest request) =>
		System.Text.Json.JsonSerializer.Serialize(request, GovernanceStateJson.Options);

	// ===== HIGH-3: a poisoned timestamp must not fail startup =====

	[Fact]
	public async Task GetActiveAsync_RowWithOutOfRangeTimestamp_SkipsRowInsteadOfThrowing()
	{
		var healthy = CreateRequest();
		var poisoned = CreateRequest();

		await InsertRawRowAsync(
			healthy.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(healthy), DateTimeOffset.UtcNow.UtcTicks);

		// long.MaxValue ticks is outside DateTimeOffset's range: converting it throws
		// ArgumentOutOfRangeException. Before the fix this threw inside ToListAsync — outside
		// any per-row guard — so GetActiveAsync faulted and took host startup down with it.
		await InsertRawRowAsync(
			poisoned.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(poisoned), long.MaxValue);

		var active = await CreateDurableStore().GetActiveAsync(CancellationToken.None);

		active.Should().ContainSingle()
			.Which.Request.EscalationId.Should().Be(healthy.EscalationId);
	}

	[Fact]
	public async Task RehydratePendingEscalationsAsync_PoisonedRowPresent_StillRestoresHealthyEscalations()
	{
		var healthy = CreateRequest();
		await InsertRawRowAsync(
			healthy.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(healthy), DateTimeOffset.UtcNow.UtcTicks);
		await InsertRawRowAsync(
			Guid.NewGuid(), nameof(EscalationPersistedStatus.Pending),
			"{ this is not json", DateTimeOffset.UtcNow.UtcTicks);

		var service = CreateService(CreateDurableStore());
		var restored = await service.RehydratePendingEscalationsAsync(CancellationToken.None);

		restored.Should().Be(1);
		(await service.GetPendingEscalationAsync(healthy.EscalationId, CancellationToken.None))
			.Should().NotBeNull();
	}

	// ===== HIGH-2: invariant-violating rows must not rehydrate =====

	[Fact]
	public async Task RehydratePendingEscalationsAsync_EmptyRosterWithTimeoutApprove_IsSkipped()
	{
		// The exact fail-open shape: nobody can vote, and AllOf would treat "nobody pending" as
		// vacuously unanimous while TimeoutAction.Approve grants it silently.
		var invalid = CreateRequest(approvers: [], timeoutAction: EscalationTimeoutAction.Approve);
		await InsertRawRowAsync(
			invalid.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(invalid), DateTimeOffset.UtcNow.UtcTicks);

		var service = CreateService(CreateDurableStore());
		var restored = await service.RehydratePendingEscalationsAsync(CancellationToken.None);

		restored.Should().Be(0);
		(await service.GetPendingEscalationAsync(invalid.EscalationId, CancellationToken.None))
			.Should().BeNull();
		(await service.GetOutcomeAsync(invalid.EscalationId, CancellationToken.None))
			.Should().BeNull();
	}

	[Fact]
	public async Task RehydratePendingEscalationsAsync_ZeroQuorumThreshold_IsSkipped()
	{
		var invalid = CreateRequest() with
		{
			ApprovalStrategy = ApprovalStrategyType.Quorum,
			QuorumThreshold = 0
		};
		await InsertRawRowAsync(
			invalid.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(invalid), DateTimeOffset.UtcNow.UtcTicks);

		var restored = await CreateService(CreateDurableStore())
			.RehydratePendingEscalationsAsync(CancellationToken.None);

		restored.Should().Be(0);
	}

	[Fact]
	public async Task RehydratePendingEscalationsAsync_TimeoutBeyondDelayCeiling_IsSkipped()
	{
		// Above the ceiling the resumed Task.Delay throws, RunTimeoutAsync's catch-all swallows
		// it, and the escalation becomes immortal — pending forever with no expiry.
		var invalid = CreateRequest(timeoutSeconds: int.MaxValue);
		await InsertRawRowAsync(
			invalid.EscalationId, nameof(EscalationPersistedStatus.Pending),
			SerializeRequest(invalid), DateTimeOffset.UtcNow.UtcTicks);

		var restored = await CreateService(CreateDurableStore())
			.RehydratePendingEscalationsAsync(CancellationToken.None);

		restored.Should().Be(0);
	}

	[Fact]
	public async Task QueueEscalationAsync_TimeoutBeyondDelayCeiling_IsRejectedAtCreation()
	{
		var service = CreateService(new NullEscalationStateStore());

		var act = () => service.QueueEscalationAsync(
			CreateRequest(timeoutSeconds: int.MaxValue), CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	// ===== HIGH-4: concurrent approvals must not lose a decision =====

	[Fact]
	public async Task SubmitDecisionAsync_ConcurrentNonResolvingApprovals_PersistsEveryDecision()
	{
		var request = CreateRequest(approvers: ["approver-1", "approver-2", "approver-3"]);
		var store = CreateDurableStore();
		var service = CreateService(store);
		await service.QueueEscalationAsync(request, CancellationToken.None);

		// Two approvals racing. Each builds its snapshot under the state lock; before the fix
		// the durable write happened after releasing it, so the two writes could land out of
		// order and last-write-wins persisted the shorter list — silently losing an approval
		// that in-memory state still showed.
		await Task.WhenAll(
			Task.Run(() => service.SubmitDecisionAsync(request.EscalationId, Approval("approver-1"), CancellationToken.None)),
			Task.Run(() => service.SubmitDecisionAsync(request.EscalationId, Approval("approver-2"), CancellationToken.None)));

		var active = await store.GetActiveAsync(CancellationToken.None);
		var snapshot = active.Should().ContainSingle().Subject;
		snapshot.Decisions.Should().HaveCount(2);
		snapshot.Decisions.Select(d => d.ApproverName)
			.Should().BeEquivalentTo(["approver-1", "approver-2"]);
	}

	// ===== MED: tampered outcomes are never re-driven =====

	[Fact]
	public async Task ReconcileStuckEscalationsAsync_OutcomeSealDoesNotVerify_RefusesToReDrive()
	{
		var request = CreateRequest(approvers: ["approver-1"]);
		var store = CreateDurableStore();
		await store.SavePendingAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
		await store.MarkResolvedPendingAuditAsync(
			new EscalationOutcome
			{
				EscalationId = request.EscalationId,
				IsApproved = true,
				Decisions = [],
				ResolutionType = EscalationResolutionType.Approved,
				ResolvedAt = DateTimeOffset.UtcNow,
				Approvers = request.Approvers
			},
			CancellationToken.None);

		// Simulates a row edited outside the process: the payload no longer matches its seal.
		_sealer.ForceVerificationFailure = true;

		var reconcile = await CreateService(store)
			.ReconcileStuckEscalationsAsync(CancellationToken.None);

		reconcile.Recovered.Should().BeEmpty();
		_auditStore.Verify(
			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Never,
			"a verdict that fails seal verification must never reach the hash-chained audit log");
	}

	// ===== MED: rehydrated + expired + TimeoutAction.Approve must not auto-approve =====

	[Fact]
	public async Task RehydratedEscalation_ExpiredDuringDowntimeWithTimeoutApprove_ResolvesDenied()
	{
		var request = CreateRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.Approve);
		var store = CreateDurableStore();
		// Created well before the deadline elapsed — the "queued before a long deploy" shape.
		await store.SavePendingAsync(request, DateTimeOffset.UtcNow.AddMinutes(-30), CancellationToken.None);

		var service = CreateService(store);
		(await service.RehydratePendingEscalationsAsync(CancellationToken.None)).Should().Be(1);

		using var cts = new CancellationTokenSource(EscalationTestDeadlines.BackgroundWork);
		EscalationOutcome? outcome = null;
		while (outcome is null)
		{
			outcome = await service.GetOutcomeAsync(request.EscalationId, CancellationToken.None);
			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(20, cts.Token);
		}

		outcome.ResolutionType.Should().Be(EscalationResolutionType.TimedOut);
		outcome.IsApproved.Should().BeFalse(
			"an escalation that expired while the host was down was seen by no approver, and " +
			"rehydration re-sends no notifications, so granting it would be a fail-open");
	}

	// ===== Path containment =====

	[Fact]
	public void Resolve_PathEscapingApplicationDirectory_Throws()
	{
		var root = Path.Combine(Path.GetTempPath(), $"gov-root-{Guid.NewGuid():N}");

		var act = () => GovernanceStatePaths.Resolve("../outside/governance-state.db", root);

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Resolve_RelativePathUnderApplicationDirectory_ReturnsAbsolutePath()
	{
		var root = Path.Combine(Path.GetTempPath(), $"gov-root-{Guid.NewGuid():N}");

		var resolved = GovernanceStatePaths.Resolve(".agent-state/governance-state.db", root);

		resolved.Should().StartWith(Path.GetFullPath(root));
		resolved.Should().EndWith("governance-state.db");
	}

	[Fact]
	public void Resolve_CaseVariantSiblingDirectory_FollowsHostFilesystemCaseRules()
	{
		// The containment check must use the HOST's case rules, not a hardcoded case-insensitive
		// comparison. On Linux — where this ships in containers — /app and /App are different
		// directories, so a case-insensitive check would accept a path that genuinely escapes the
		// application directory and let the approval-verdict database be written outside it.
		var stem = Path.Combine(Path.GetTempPath(), $"gov-root-{Guid.NewGuid():N}");
		var root = stem + "-lower";
		var caseVariant = stem + "-LOWER";

		var act = () => GovernanceStatePaths.Resolve(
			Path.Combine(caseVariant, "governance-state.db"), root);

		if (OperatingSystem.IsWindows())
		{
			// Windows paths are case-insensitive, so the variant IS the same directory.
			act.Should().NotThrow();
		}
		else
		{
			act.Should().Throw<ArgumentException>(
				"under ordinal (Linux) semantics the case variant is a different directory, so it " +
				"escapes the application directory and must be rejected");
		}
	}

	[Fact]
	public void Resolve_ApplicationDirectoryItself_Throws()
	{
		// The configured value must name a database FILE. Containment alone would admit the root,
		// which is a directory — caught here rather than as an opaque SQLite open failure later.
		var root = Path.Combine(Path.GetTempPath(), $"gov-root-{Guid.NewGuid():N}");

		var act = () => GovernanceStatePaths.Resolve(".", root);

		act.Should().Throw<ArgumentException>();
	}

	/// <summary>Minimal context factory over fixed options.</summary>
	private sealed class TestContextFactory(DbContextOptions<GovernanceStateDbContext> options)
		: IDbContextFactory<GovernanceStateDbContext>
	{
		public GovernanceStateDbContext CreateDbContext() => new(options);
	}
}
