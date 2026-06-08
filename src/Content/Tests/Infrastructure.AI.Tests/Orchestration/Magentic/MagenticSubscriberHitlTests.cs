using System.Linq;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Moq;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 acceptance test for the HITL plan-review dispatch path through the
/// event subscriber. Verifies that a <see cref="RequestInfoEvent"/> carrying a
/// <see cref="MagenticPlanReviewRequest"/> opens a plan-review span, calls the
/// bridge, and returns an <see cref="ExternalResponse"/> with the
/// <see cref="MagenticPlanReviewResponse"/> the bridge produced.
/// </summary>
[Collection("MagenticTraceCollection")]
public sealed class MagenticSubscriberHitlTests
{
    [Fact]
    public async Task RequestInfoEvent_routes_through_bridge_and_returns_response()
    {
        using var captured = new MagenticTestHelpers.CapturedSpans();
        var subscriber = MagenticTestHelpers.BuildSubscriber(out var bridge, out _);
        bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true });

        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);

        var planReview = new MagenticPlanReviewRequest(
            MagenticTestHelpers.AsLedger("draft plan"),
            MagenticTestHelpers.BuildLedger(nextSpeaker: "agent", instructionOrQuestion: "review"),
            IsStalled: true);

        var port = new RequestPortInfo(
            new TypeId(typeof(MagenticPlanReviewRequest)),
            new TypeId(typeof(MagenticPlanReviewResponse)),
            "magentic.plan_review");
        var externalRequest = new ExternalRequest(port, Guid.NewGuid().ToString(), new PortableValue(planReview));
        var evt = new RequestInfoEvent(externalRequest);

        var response = await subscriber.ProcessEventAsync(evt, default);
        subscriber.EndWorkflow(MagenticConventions.CompletionReasonSatisfied);

        response.Should().NotBeNull();
        bridge.Verify(b => b.RequestPlanReviewAsync(
            It.Is<MagenticPlanReviewInput>(i => i.IsStalled),
            It.IsAny<CancellationToken>()), Times.Once);
        captured.Activities.Should().Contain(a => a.DisplayName == MagenticConventions.SpanNamePlanReview);
        subscriber.PlanReviewsExecuted.Should().Be(1);
    }
}

#pragma warning restore MAAIW001
