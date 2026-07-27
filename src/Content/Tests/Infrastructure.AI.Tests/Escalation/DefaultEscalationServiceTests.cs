using System.Diagnostics;
using System.IO;
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

		// Wait for the escalation to be registered before submitting: a decision submitted
		// pre-registration is silently dropped (DefaultEscalationService.cs:88-92) and the
		// request then resolves only via its 5-minute timeout with IsApproved=false — the
		// same race documented on RequestEscalationAsync_AfterRegistrationSignal_*.
		await WaitForRegistrationAsync(request.EscalationId);

		// Registered but undecided: the blocking call must still be pending.
		task.IsCompleted.Should().BeFalse("RequestEscalationAsync should block until resolved");

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

		var submitResult = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);
		var outcome = await task;

		submitResult.Status.Should().Be(EscalationDecisionStatus.Resolved,
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
	public async Task SubmitDecisionAsync_ResolvingDecision_ReturnsResolvedCarryingOutcome()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.Resolved);
		result.Outcome.Should().NotBeNull(
			"a Resolved result must carry the final verdict for the caller");
		result.Outcome!.IsApproved.Should().BeTrue();
		result.Outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved);

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
	public async Task SubmitDecisionAsync_PartialDecisionUnderAllOf_ReturnsDecisionRecorded()
	{
		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded,
			"a recorded decision that does not satisfy the strategy must be reported as recorded-but-pending, not conflated with unknown/unauthorized");
		result.Outcome.Should().BeNull("only a Resolved result carries an outcome");
		_auditStore.Verify(
			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_notifier.Verify(
			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task SubmitDecisionAsync_UnknownEscalationId_ReturnsUnknownEscalation()
	{
		var result = await _sut.SubmitDecisionAsync(
			Guid.NewGuid(), CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.UnknownEscalation,
			"an HTTP layer must be able to map a missing escalation to 404, distinct from the other non-resolving cases");
		_auditStore.Verify(
			a => a.RecordDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task SubmitDecisionAsync_NonRosterApprover_RejectedNotRecordedNotEvaluated()
	{
		var request = CreateTestRequest(); // roster: approver-1, approver-2
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval("mallory"), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.ApproverNotAuthorized,
			"a decision from an identity outside the approver roster must be rejected as unauthorized, mappable to 403");
		_auditStore.Verify(
			a => a.RecordDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Never);
		_anyOfStrategy.Verify(
			s => s.EvaluateDecision(It.IsAny<EscalationRequest>(), It.IsAny<IReadOnlyList<ApproverDecision>>()),
			Times.Never);
	}

	[Fact]
	public async Task SubmitDecisionAsync_OutcomeAuditThrows_FailsClosed_NotReportedResolved()
	{
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		_auditStore
			.Setup(a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new IOException("audit sink unavailable"));
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var act = () => _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		await act.Should().ThrowAsync<IOException>(
			"a resolved escalation whose outcome cannot be durably audited must fail closed, not deliver an unaudited approval");
		_notifier.Verify(
			n => n.NotifyEscalationResolvedAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task QueueEscalationAsync_EmptyApproverRoster_FailsClosed()
	{
		var request = CreateTestRequest(approvers: Array.Empty<string>());

		var act = () => _sut.QueueEscalationAsync(request, CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>(
			"an escalation with no approvers can never be legitimately approved and must be rejected at creation");
	}

	[Fact]
	public async Task RequestEscalationAsync_EmptyApproverRoster_FailsClosed()
	{
		var request = CreateTestRequest(approvers: Array.Empty<string>());

		var act = () => _sut.RequestEscalationAsync(request, CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>(
			"a blocking escalation with no approvers must fail closed rather than await an unapprovable request");
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

		var results = await Task.WhenAll(tasks);
		results.Count(r => r.Status == EscalationDecisionStatus.Resolved).Should().Be(1,
			"exactly one concurrent decision should resolve the escalation");
	}

	// ===== CancelEscalation =====

	[Fact]
	public async Task CancelEscalationAsync_ResolvesWithDenied()
	{
		var request = CreateTestRequest();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.CancelEscalationAsync(
			request.EscalationId, "No longer needed", "admin@contoso.com", CancellationToken.None);

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
			request.EscalationId, "Too late", "admin@contoso.com", CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task CancelEscalationAsync_StampsActorAndRosterOnOutcome()
	{
		// The force-denial must be attributable in the durable outcome audit (CancelledBy) and
		// must retain the roster so roster-private reads keep working after resolution.
		var request = CreateTestRequest(approvers: ["approver-1", "approver-2"]);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.CancelEscalationAsync(
			request.EscalationId, "No longer needed", "admin@contoso.com", CancellationToken.None);

		outcome.CancelledBy.Should().Be("admin@contoso.com");
		outcome.Approvers.Should().BeEquivalentTo("approver-1", "approver-2");
	}

	[Fact]
	public async Task SubmitDecisionAsync_ResolvedOutcome_CarriesRoster()
	{
		var request = CreateTestRequest(approvers: ["approver-1", "approver-2"]);
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.Resolved);
		result.Outcome!.Approvers.Should().BeEquivalentTo("approver-1", "approver-2");
		result.Outcome.CancelledBy.Should().BeNull("decision resolutions have no cancelling actor");
	}

	[Fact]
	public async Task SubmitDecisionAsync_DuplicateApproverWithOppositeVerdict_ReturnsConflictWithoutRecording()
	{
		// A changed vote must be rejected honestly (ConflictingDecision → 409), not silently
		// dropped while echoing "recorded": no audit append, no strategy re-evaluation.
		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var first = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval("approver-1"), CancellationToken.None);
		var changed = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateDenial("APPROVER-1"), CancellationToken.None);

		first.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded);
		changed.Status.Should().Be(EscalationDecisionStatus.ConflictingDecision);
		_auditStore.Verify(
			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_allOfStrategy.Verify(
			s => s.EvaluateDecision(It.IsAny<EscalationRequest>(), It.IsAny<IReadOnlyList<ApproverDecision>>()),
			Times.Once);
	}

	[Fact]
	public async Task SubmitDecisionAsync_OutcomeAuditFails_EscalationStaysObservableAndVerdictUnpublished()
	{
		// Resolution ordering contract: the outcome is durably audited FIRST; on failure the
		// escalation must remain in the active set (still observable as pending) and the verdict
		// must never be published to GetOutcomeAsync. The old remove-then-audit order made a
		// failed-audit escalation vanish from every reader.
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		_auditStore
			.Setup(a => a.RecordOutcomeAsync(It.IsAny<EscalationOutcome>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("audit store unavailable"));
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var act = () => _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		(await _sut.GetOutcomeAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull("an unaudited verdict must never be served");
		(await _sut.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().NotBeNull("a resolved-but-unaudited escalation must stay observable, not vanish");
	}

	[Fact]
	public async Task SubmitDecisionAsync_ResolvedEscalation_OutcomeRetrievableImmediately()
	{
		// The 202→poll contract: once a decision resolves, GetOutcomeAsync must serve the
		// verdict (published before the active-set removal, after the audit write).
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.Resolved);
		(await _sut.GetOutcomeAsync(request.EscalationId, CancellationToken.None))
			.Should().NotBeNull();
		(await _sut.GetPendingEscalationAsync(request.EscalationId, CancellationToken.None))
			.Should().BeNull("resolution removes the escalation from the active set");
	}

	[Fact]
	public async Task SubmitDecisionAsync_DuplicateApprover_IgnoredWithoutSecondAuditRecord()
	{
		// One recorded decision per approver per escalation: a retried submission (even with
		// different casing) must not append to the decision list, must not re-run the strategy,
		// and must not grow the audit trail — it reports DecisionRecorded and stops.
		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var first = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval("approver-1"), CancellationToken.None);
		var repeat = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval("APPROVER-1"), CancellationToken.None);

		first.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded);
		repeat.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded);
		_auditStore.Verify(
			a => a.RecordDecisionAsync(request.EscalationId, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()),
			Times.Once);
		_allOfStrategy.Verify(
			s => s.EvaluateDecision(It.IsAny<EscalationRequest>(), It.IsAny<IReadOnlyList<ApproverDecision>>()),
			Times.Once);
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
	public async Task GetPendingEscalationsAsync_ApproverCasedDifferentlyFromRoster_SeesPendingEscalation()
	{
		// Regression (roster case-sensitivity mismatch): SubmitDecisionAsync matched the roster
		// OrdinalIgnoreCase while GetPendingEscalationsAsync used case-sensitive Contains — an
		// approver whose identity differed from the roster entry only by casing could decide
		// an escalation they could never see in their pending list.
		var request = CreateTestRequest(); // roster: approver-1, approver-2
		SetupStrategyNeverResolves(ApprovalStrategyType.AnyOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var pending = await _sut.GetPendingEscalationsAsync("APPROVER-1", CancellationToken.None);

		pending.Should().ContainSingle(r => r.EscalationId == request.EscalationId,
			"the pending view must use the same case-insensitive roster match as the decide path");
	}

	[Fact]
	public async Task SubmitDecisionAsync_ApproverCasedDifferentlyFromRoster_CanDecide()
	{
		// Companion to the pending-view casing test: the same differently-cased approver must
		// also be able to decide, proving see-and-decide agree on roster membership semantics.
		var request = CreateTestRequest(); // roster: approver-1, approver-2
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var result = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval("APPROVER-1"), CancellationToken.None);

		result.Status.Should().Be(EscalationDecisionStatus.Resolved);
		result.Outcome!.IsApproved.Should().BeTrue();
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

	// ===== GetOutcome (resolved-outcome cache) =====

	[Fact]
	public async Task GetOutcomeAsync_WhilePending_ReturnsNull()
	{
		var request = CreateTestRequest(strategy: ApprovalStrategyType.AllOf);
		SetupStrategyNeverResolves(ApprovalStrategyType.AllOf);
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.GetOutcomeAsync(request.EscalationId, CancellationToken.None);

		outcome.Should().BeNull("a still-pending escalation has no resolved verdict to report");
	}

	[Fact]
	public async Task GetOutcomeAsync_UnknownId_ReturnsNull()
	{
		var outcome = await _sut.GetOutcomeAsync(Guid.NewGuid(), CancellationToken.None);

		outcome.Should().BeNull();
	}

	[Fact]
	public async Task GetOutcomeAsync_AfterApprovalResolves_ReturnsResolvedOutcome()
	{
		// Directly exercises the real _resolvedOutcomes population that the plan-executor resume
		// bridge relies on: once a decision resolves the escalation, GetOutcomeAsync must surface it.
		var request = CreateTestRequest();
		SetupStrategyResolvesOnFirstApproval();
		await _sut.QueueEscalationAsync(request, CancellationToken.None);

		var submitted = await _sut.SubmitDecisionAsync(
			request.EscalationId, CreateApproval(), CancellationToken.None);
		submitted.Status.Should().Be(EscalationDecisionStatus.Resolved);

		var outcome = await _sut.GetOutcomeAsync(request.EscalationId, CancellationToken.None);

		outcome.Should().NotBeNull("the resolved verdict must be retrievable after resolution");
		outcome!.EscalationId.Should().Be(request.EscalationId);
		outcome.IsApproved.Should().BeTrue();
		outcome.ResolutionType.Should().Be(EscalationResolutionType.Approved);
	}

	[Fact]
	public async Task GetOutcomeAsync_AfterTimeoutResolves_ReturnsResolvedOutcome()
	{
		// The resolved-outcome cache must be populated on every resolution path, including timeout.
		var request = CreateTestRequest(timeoutSeconds: 1, timeoutAction: EscalationTimeoutAction.Deny);

		await _sut.RequestEscalationAsync(request, CancellationToken.None);

		var outcome = await _sut.GetOutcomeAsync(request.EscalationId, CancellationToken.None);

		outcome.Should().NotBeNull();
		outcome!.ResolutionType.Should().Be(EscalationResolutionType.TimedOut);
		outcome.IsApproved.Should().BeFalse();
	}
}
