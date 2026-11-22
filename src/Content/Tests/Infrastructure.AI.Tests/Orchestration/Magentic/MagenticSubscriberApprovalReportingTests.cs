using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Domain.AI.Escalation;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Moq;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// #395: the approver of a Magentic plan review is told how the plan they approved ended. The outcome is
/// only known when the workflow ends or replaces the plan, so each approval is held open until then and
/// closed exactly once.
/// </summary>
[Collection("MagenticTraceCollection")]
public sealed class MagenticSubscriberApprovalReportingTests
{
    private readonly RecordingApprovalReporter _reporter = new();
    private readonly MagenticEventSubscriber _subscriber;
    private readonly Mock<IMagenticPlanReviewBridge> _bridge;

    public MagenticSubscriberApprovalReportingTests()
    {
        _subscriber = MagenticTestHelpers.BuildSubscriber(out _bridge, out _, approvalReporter: _reporter);
        var request = MagenticTestHelpers.BuildRequest();
        _subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);
    }

    private static RequestInfoEvent PlanReview(bool stalled = false)
    {
        var review = new MagenticPlanReviewRequest(
            MagenticTestHelpers.AsLedger("the plan"), MagenticTestHelpers.BuildLedger(), IsStalled: stalled);
        var port = new RequestPortInfo(
            new TypeId(typeof(MagenticPlanReviewRequest)),
            new TypeId(typeof(MagenticPlanReviewResponse)),
            "magentic.plan_review");
        return new RequestInfoEvent(new ExternalRequest(port, Guid.NewGuid().ToString(), new PortableValue(review)));
    }

    private async Task<Guid> ApprovePlanAsync(bool stalled = false)
    {
        var id = Guid.NewGuid();
        _bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true, EscalationId = id });
        await _subscriber.ProcessEventAsync(PlanReview(stalled), default);
        return id;
    }

    private RecordingApprovalReporter.Report SingleReport()
    {
        var report = _reporter.Reports.Should().ContainSingle().Subject;
        report.ReportedBy.Should().Be(MagenticEventSubscriber.ApprovalReportedBy);
        report.HadFailureKey.Should().BeFalse("nothing ever looks a plan review's failure-memory key up");
        return report;
    }

    [Fact]
    public async Task ApprovedPlan_IsNotReportedBeforeTheWorkflowEnds()
    {
        await ApprovePlanAsync();

        _reporter.Reports.Should().BeEmpty("an approval only hands the plan back; nothing has run yet");
    }

    [Fact]
    public async Task ApprovedPlan_WorkflowSatisfied_ReportsSucceeded()
    {
        var id = await ApprovePlanAsync();

        await _subscriber.NotifyCompletedAsync(MagenticConventions.CompletionReasonSatisfied, default);

        SingleReport().Should().Match<RecordingApprovalReporter.Report>(r =>
            r.EscalationId == id && r.Status == EscalationExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(MagenticConventions.CompletionReasonRoundLimit)]
    [InlineData(MagenticConventions.CompletionReasonResetLimit)]
    public async Task ApprovedPlan_WorkflowStopsAtALimit_ReportsFailedNotSucceeded(string completionReason)
    {
        // The caller gets success for a run that stopped at its limit, but the plan the approver signed
        // off on did not finish.
        await ApprovePlanAsync();

        await _subscriber.NotifyCompletedAsync(completionReason, default);

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.Failed);
        report.FailureText.Should().Contain(completionReason);
    }

    [Fact]
    public async Task ApprovedPlan_WorkflowCancelled_ReportsNeverExecutedRunCancelled()
    {
        await ApprovePlanAsync();

        await _subscriber.NotifyFailedAsync(MagenticEventSubscriber.CancelledErrorCode, default);

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.NeverExecuted);
        report.NotExecutedReason.Should().Be(EscalationNotExecutedReason.RunCancelled);
    }

    [Fact]
    public async Task ApprovedPlan_WorkflowFails_ReportsFailedNamingTheErrorCode()
    {
        await ApprovePlanAsync();

        await _subscriber.NotifyFailedAsync("magentic.unhandled_exception", default);

        var report = SingleReport();
        report.Status.Should().Be(EscalationExecutionStatus.Failed);
        report.FailureText.Should().Contain("magentic.unhandled_exception");
    }

    [Fact]
    public async Task ANewPlanReview_ClosesTheEarlierApprovalAsFailed_AndTheNewApprovalGetsTheEnding()
    {
        var first = await ApprovePlanAsync();
        var second = await ApprovePlanAsync(stalled: true);
        await _subscriber.NotifyCompletedAsync(MagenticConventions.CompletionReasonSatisfied, default);

        _reporter.Reports.Should().SatisfyRespectively(
            superseded =>
            {
                superseded.EscalationId.Should().Be(first);
                superseded.Status.Should().Be(EscalationExecutionStatus.Failed);
                superseded.FailureText.Should().Be(MagenticEventSubscriber.SupersededFailureText);
            },
            ending =>
            {
                ending.EscalationId.Should().Be(second);
                ending.Status.Should().Be(EscalationExecutionStatus.Succeeded);
            });
    }

    [Fact]
    public async Task ARevisedPlan_ReportsNothing()
    {
        _bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = false, RevisionFeedback = "shorter" });
        await _subscriber.ProcessEventAsync(PlanReview(), default);

        await _subscriber.NotifyCompletedAsync(MagenticConventions.CompletionReasonSatisfied, default);

        _reporter.Reports.Should().BeEmpty("nothing was approved, so there is no approver to report to");
    }

    [Fact]
    public async Task AnApprovalWithNoEscalationBehindIt_ReportsNothing()
    {
        // A custom bridge that approves without a human escalation has no approver to report to.
        _bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true });
        await _subscriber.ProcessEventAsync(PlanReview(), default);

        await _subscriber.NotifyCompletedAsync(MagenticConventions.CompletionReasonSatisfied, default);

        _reporter.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task AnApproval_IsReportedOnce_EvenIfTheRunReportsItsEndTwice()
    {
        await ApprovePlanAsync();

        await _subscriber.NotifyFailedAsync("magentic.error", default);
        await _subscriber.NotifyCompletedAsync(MagenticConventions.CompletionReasonSatisfied, default);

        SingleReport().Status.Should().Be(EscalationExecutionStatus.Failed);
    }

    [Fact]
    public async Task TheEndingIsReported_EvenWhenTheRunsOwnTokenIsCancelled()
    {
        // The usual reason to be ending is that the run's token was cancelled; the report must not ride on it.
        await ApprovePlanAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await _subscriber.NotifyFailedAsync(MagenticEventSubscriber.CancelledErrorCode, cancelled.Token);

        SingleReport().NotExecutedReason.Should().Be(EscalationNotExecutedReason.RunCancelled);
    }
}
