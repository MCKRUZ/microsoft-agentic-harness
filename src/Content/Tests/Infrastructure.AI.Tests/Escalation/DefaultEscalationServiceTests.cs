using System.Diagnostics;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="DefaultEscalationService"/>.
/// Verifies escalation lifecycle: creation, strategy evaluation, timeout racing,
/// cancellation propagation, notification dispatch, and audit recording.
/// </summary>
public sealed class DefaultEscalationServiceTests : IDisposable
{
	private readonly Mock<IEscalationNotifier> _notifier = new();
	private readonly Mock<IEscalationAuditStore> _auditStore = new();
	private readonly Mock<IApprovalStrategy> _anyOfStrategy = new();
	private readonly Mock<IApprovalStrategy> _allOfStrategy = new();
	private readonly DefaultEscalationService _sut;

	public DefaultEscalationServiceTests()
	{
		_anyOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AnyOf);
		_allOfStrategy.Setup(s => s.StrategyType).Returns(ApprovalStrategyType.AllOf);

		var services = new ServiceCollection();
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AnyOf, (_, _) => _anyOfStrategy.Object);
		services.AddKeyedSingleton<IApprovalStrategy>(
			ApprovalStrategyType.AllOf, (_, _) => _allOfStrategy.Object);
		var serviceProvider = services.BuildServiceProvider();

		var configMonitor = new Mock<IOptionsMonitor<EscalationConfig>>();
		configMonitor.Setup(m => m.CurrentValue).Returns(new EscalationConfig
		{
			Enabled = true,
			DefaultTimeoutSeconds = 300,
			DefaultApprovalStrategy = "AnyOf"
		});

		_sut = new DefaultEscalationService(
			serviceProvider,
			_notifier.Object,
			_auditStore.Object,
			configMonitor.Object,
			NullLogger<DefaultEscalationService>.Instance);
	}

	public void Dispose() => _sut.Dispose();

	// --- Helpers ---

	private static EscalationRequest CreateTestRequest(
		EscalationPriority priority = EscalationPriority.Blocking,
		ApprovalStrategyType strategy = ApprovalStrategyType.AnyOf,
		int timeoutSeconds = 300,
		EscalationTimeoutAction timeoutAction = EscalationTimeoutAction.DenyAndEscalate,
		IReadOnlyList<string>? approvers = null) =>
		new()
		{
			EscalationId = Guid.NewGuid(),
			AgentId = "test-agent",
			ToolName = "dangerous-tool",
			Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
			Description = "Test escalation",
			RiskLevel = RiskLevel.High,
			Priority = priority,
			ApprovalStrategy = strategy,
			Approvers = approvers ?? ["approver-1", "approver-2"],
			TimeoutSeconds = timeoutSeconds,
			TimeoutAction = timeoutAction,
			RequestedAt = DateTimeOffset.UtcNow
		};

	private static ApproverDecision CreateApproval(string approverName = "approver-1") =>
		new()
		{
			ApproverName = approverName,
			Approved = true,
			Reason = "Looks good",
			RespondedAt = DateTimeOffset.UtcNow
		};

	private static ApproverDecision CreateDenial(string approverName = "approver-1") =>
		new()
		{
			ApproverName = approverName,
			Approved = false,
			Reason = "Too risky",
			RespondedAt = DateTimeOffset.UtcNow
		};

	private void SetupStrategyResolvesOnFirstApproval()
	{
		_anyOfStrategy
			.Setup(s => s.EvaluateDecision(
				It.IsAny<EscalationRequest>(),
				It.IsAny<IReadOnlyList<ApproverDecision>>()))
			.Returns((EscalationRequest _, IReadOnlyList<ApproverDecision> decisions) =>
				decisions.Any(d => d.Approved)
					? new ApprovalEvaluation
					{
						IsResolved = true,
						IsApproved = true,
						PendingApprovers = []
					}
					: new ApprovalEvaluation
					{
						IsResolved = false,
						IsApproved = false,
						PendingApprovers = ["pending"]
					});
	}

	/// <summary>
	/// Polls until the escalation is registered in the service, or fails fast on timeout.
	/// </summary>
	/// <remarks>
	/// <see cref="DefaultEscalationService.RequestEscalationAsync"/> registers the escalation
	/// synchronously before it begins awaiting the outcome, exposing it via
	/// <see cref="DefaultEscalationService.GetPendingEscalationAsync"/>. A bare
	/// <c>Task.Delay</c> can lose the race under thread-pool starvation, after which
	/// <c>SubmitDecisionAsync</c> silently drops the decision and the test stalls for the
	/// full request timeout. Polling the registration signal removes that race and fails in
	/// seconds (not minutes) if registration genuinely never happens.
	/// </remarks>
	private async Task WaitForRegistrationAsync(Guid escalationId)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (true)
		{
			var pending = await _sut.GetPendingEscalationAsync(escalationId, CancellationToken.None);
			if (pending is not null)
				return;

			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(10, cts.Token);
		}
	}

	private void SetupStrategyNeverResolves(ApprovalStrategyType strategyType)
	{
		var mock = strategyType == ApprovalStrategyType.AllOf ? _allOfStrategy : _anyOfStrategy;
		mock.Setup(s => s.EvaluateDecision(
				It.IsAny<EscalationRequest>(),
				It.IsAny<IReadOnlyList<ApproverDecision>>()))
			.Returns(new ApprovalEvaluation
			{
				IsResolved = false,
				IsApproved = false,
				PendingApprovers = ["pending"]
			});
	}

	// ===== RequestEscalationAsync =====

	[Fact]
	public async Task RequestEscalationAsync_CreatesEscalation_NotifiesApprovers()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();

		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
		await WaitForRegistrationAsync(request.EscalationId);

		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		var outcome = await task;

		outcome.IsApproved.Should().BeTrue();
		_notifier.Verify(
			n => n.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task RequestEscalationAsync_BlockingMode_AwaitsOutcome()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();

		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));

		var completedEarly = await Task.WhenAny(task, Task.Delay(200)) == task;
		completedEarly.Should().BeFalse("RequestEscalationAsync should block until resolved");

		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		var outcome = await task;

		outcome.Should().NotBeNull();
		outcome.IsApproved.Should().BeTrue();
	}

	[Fact]
	public async Task RequestEscalationAsync_AuditsRequest()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();

		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
		await WaitForRegistrationAsync(request.EscalationId);

		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);
		await task;

		_auditStore.Verify(
			a => a.RecordRequestAsync(request, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task RequestEscalationAsync_AfterRegistrationSignal_DecisionIsRecordedNotDropped()
	{
		// Regression: the prior `Task.Delay(50)` could lose the registration race under
		// thread-pool starvation, causing SubmitDecisionAsync to silently drop the decision
		// (DefaultEscalationService.cs:88-92) and the request to resolve only via timeout.
		// Waiting on the registration signal guarantees the escalation is in-flight before
		// submitting, so the decision is recorded and resolves the escalation.
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();

		var task = Task.Run(() => _sut.RequestEscalationAsync(request, CancellationToken.None));
		await WaitForRegistrationAsync(request.EscalationId);

		var submitOutcome = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);
		var outcome = await task;

		submitOutcome.Should().NotBeNull(
			"the decision must hit a registered escalation, not be dropped as unknown");
		outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved,
			"the escalation must resolve from the submitted approval, not from a timeout");
		_auditStore.Verify(
			a => a.RecordDecisionAsync(
				request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	// ===== QueueEscalationAsync =====

	[Fact]
	public async Task QueueEscalationAsync_ReturnsEscalationId_DoesNotBlock()
	{
		var request = CreateTestRequest();

		var sw = Stopwatch.StartNew();
		var id = await _sut.QueueEscalationAsync(request, CancellationToken.None);
		sw.Stop();

		id.Should().Be(request.EscalationId);
		sw.ElapsedMilliseconds.Should().BeLessThan(1000);
		_notifier.Verify(
			n => n.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	// ===== SubmitDecisionAsync =====

	[Fact]
	public async Task SubmitDecisionAsync_TriggersStrategyEvaluation_ReturnsOutcomeIfResolved()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		outcome.Should().NotBeNull();
		outcome!.IsApproved.Should().BeTrue();
		outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved);

		_auditStore.Verify(
			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_auditStore.Verify(
			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_notifier.Verify(
			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task SubmitDecisionAsync_PartialDecision_ReturnsNull()
	{
		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		outcome.Should().BeNull();
		_auditStore.Verify(
			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_notifier.Verify(
			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task SubmitDecisionAsync_UnknownEscalationId_ReturnsNull()
	{
		var outcome = await _sut.SubmitDecisionAsync(
			Guid.NewGuid(), CreateApproval(), CancellationToken.None);

		outcome.Should().BeNull();
		_auditStore.Verify(
			a => a.RecordDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	// ===== Timeout =====

	[Fact]
	public async Task Timeout_FiresDenyAndEscalate_CompletesWithTimedOut()
	{
		var request = CreateTestRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.DenyAndEscalate);

		var outcome = await _sut.RequestEscalationAsync(request, CancellationToken.None);

		outcome.ResolutionType.Should().Be(EscalationResolutionType.TimedOut);
		outcome.IsApproved.Should().BeFalse();
		_auditStore.Verify(
			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Timeout_CallerCancelled_PropagatesCancellation()
	{
		var request = CreateTestRequest(timeoutSeconds: 300);
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		var act = () => _sut.RequestEscalationAsync(request, cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();

		var pending = await _sut.GetPendingEscalationAsync(
			request.EscalationId, CancellationToken.None);
		pending.Should().BeNull();
	}

	[Fact]
	public async Task Timeout_AuditsOutcome()
	{
		var request = CreateTestRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.Deny);

		await _sut.RequestEscalationAsync(request, CancellationToken.None);

		_auditStore.Verify(
			a => a.RecordOutcomeAsync(
				It.Is<EscalationOutcome>(o => o.ResolutionType == EscalationResolutionType.TimedOut),
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	// ===== Concurrency =====

	[Fact]
	public async Task ConcurrentDecisions_ThreadSafe_NoRaceConditions()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var tasks = Enumerable.Range(1, 3)
			.Select(i => _sut.SubmitDecisionAsync(
				request.EscalationId,
				CreateApproval($"approver-{i}"),
				CancellationToken.None))
			.ToArray();

		var outcomes = await Task.WhenAll(tasks);
		outcomes.Count(o => o is not null).Should().Be(1,
			"exactly one concurrent decision should resolve the escalation");
	}

	// ===== CancelEscalation =====

	[Fact]
	public async Task CancelEscalationAsync_ResolvesWithDenied()
	{
		var request = CreateTestRequest();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.CancelEscalationAsync(
			request.EscalationId, "No longer needed", CancellationToken.None);

		outcome.IsApproved.Should().BeFalse();
		outcome.ResolutionType.Should().Be(EscalationResolutionType.Denied);
		_auditStore.Verify(
			a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task CancelEscalationAsync_AlreadyResolved_Throws()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);
		await _sut.SubmitDecisionAsync(request.EscalationId, CreateApproval(), CancellationToken.None);

		var act = () => _sut.CancelEscalationAsync(
			request.EscalationId, "Too late", CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	// ===== GetPending =====

	[Fact]
	public async Task GetPendingEscalationsAsync_ReturnsOnlyPending()
	{
		var req1 = CreateTestRequest();
		var req2 = CreateTestRequest();
		var req3 = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();

		await _sut.QueueEscalationAsync(req1, CancellationToken.None);
		await _sut.QueueEscalationAsync(req2, CancellationToken.None);
		await _sut.QueueEscalationAsync(req3, CancellationToken.None);

		await _sut.SubmitDecisionAsync(req1.EscalationId, CreateApproval(), CancellationToken.None);

		var pending = await _sut.GetPendingEscalationsAsync("approver-1", CancellationToken.None);
		pending.Should().HaveCount(2);
		pending.Should().NotContain(r => r.EscalationId == req1.EscalationId);
	}

	[Fact]
	public async Task GetPendingEscalationAsync_ResolvedEscalation_ReturnsNull()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		var pending = await _sut.GetPendingEscalationAsync(
			request.EscalationId, CancellationToken.None);
		pending.Should().BeNull();
	}
}
