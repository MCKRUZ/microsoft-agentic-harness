using System.Linq;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 acceptance tests for the production HITL bridge. Verifies that a
/// Magentic plan-review pause is dispatched through
/// <see cref="IEscalationService.RequestEscalationAsync"/> with the correct
/// risk + priority + approver mapping, and that the outcome translates back
/// to a <see cref="MagenticPlanReviewOutcome"/> approve/revise decision.
/// </summary>
public sealed class MagenticHitlBridgeTests
{
    [Fact]
    public async Task Stalled_plan_review_routes_high_risk_to_escalation_service()
    {
        var svc = new Mock<IEscalationService>();
        EscalationRequest? capturedRequest = null;
        svc.Setup(s => s.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync((EscalationRequest req, CancellationToken _) => new EscalationOutcome
            {
                EscalationId = req.EscalationId,
                IsApproved = true,
                Decisions = new[]
                {
                    new ApproverDecision
                    {
                        ApproverName = req.Approvers[0],
                        Approved = true,
                        RespondedAt = DateTimeOffset.UtcNow
                    }
                },
                ResolutionType = EscalationResolutionType.Approved,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        var bridge = new MagenticHitlBridge(
            svc.Object,
            NullLogger<MagenticHitlBridge>.Instance,
            new FakeTimeProvider());

        var outcome = await bridge.RequestPlanReviewAsync(
            new MagenticPlanReviewInput
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "wf",
                PlanText = "plan",
                IsStalled = true,
                ProgressLedgerSummary = "stalled=true"
            },
            CancellationToken.None);

        outcome.Approved.Should().BeTrue();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RiskLevel.Should().Be(RiskLevel.High);
        capturedRequest.Priority.Should().Be(EscalationPriority.Blocking);
        capturedRequest.ToolName.Should().Be("magentic.plan_review");
        capturedRequest.Arguments.Should().ContainKey("is_stalled").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task Denied_plan_review_returns_revise_with_first_reason()
    {
        var svc = new Mock<IEscalationService>();
        svc.Setup(s => s.RequestEscalationAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest req, CancellationToken _) => new EscalationOutcome
            {
                EscalationId = req.EscalationId,
                IsApproved = false,
                Decisions = new[]
                {
                    new ApproverDecision
                    {
                        ApproverName = req.Approvers[0],
                        Approved = false,
                        Reason = "needs more detail",
                        RespondedAt = DateTimeOffset.UtcNow
                    }
                },
                ResolutionType = EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow
            });

        var bridge = new MagenticHitlBridge(svc.Object, NullLogger<MagenticHitlBridge>.Instance, new FakeTimeProvider());

        var outcome = await bridge.RequestPlanReviewAsync(
            new MagenticPlanReviewInput
            {
                WorkflowId = Guid.NewGuid(),
                WorkflowName = "wf",
                PlanText = "plan",
                IsStalled = false
            },
            CancellationToken.None);

        outcome.Approved.Should().BeFalse();
        outcome.RevisionFeedback.Should().Be("needs more detail");
    }
}
