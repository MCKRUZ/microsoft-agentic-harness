using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Bundles;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="WorkflowRunKindExecutor"/>.
/// </summary>
/// <remarks>
/// Almost all of this class is a translation: a plan's final status becomes a run's outcome. That
/// translation is the whole of its risk. A plan can end in four ways and a run in four ways, and any
/// mapping that folds two of them together tells a caller something untrue about work it paid for —
/// most damagingly by reporting a workflow parked awaiting an approval as finished.
/// </remarks>
public sealed class WorkflowRunKindExecutorTests
{
    private readonly Mock<IPlanRunExecutor> _planRunExecutor = new();

    private WorkflowRunKindExecutor BuildSut() =>
        new(_planRunExecutor.Object, NullLogger<WorkflowRunKindExecutor>.Instance);

    private static RunRecord Record(string? targetId = null) => new()
    {
        JobId = "job-1",
        Kind = RunKind.Workflow,
        TargetId = targetId ?? Guid.NewGuid().ToString(),
        OwnerId = "alice",
        TenantId = "acme",
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Running,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private void PlanEndsWith(StepExecutionStatus finalStatus) =>
        _planRunExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = new PlanId(Guid.NewGuid()),
                FinalStatus = finalStatus,
                TotalDuration = TimeSpan.FromSeconds(1),
                StepStates = []
            }));

    [Theory]
    [InlineData(StepExecutionStatus.Completed, RunStatus.Succeeded)]
    [InlineData(StepExecutionStatus.Failed, RunStatus.Failed)]
    [InlineData(StepExecutionStatus.Cancelled, RunStatus.Cancelled)]
    [InlineData(StepExecutionStatus.Blocked, RunStatus.Blocked)]
    public async Task ThePlansFinalStatusBecomesTheRunsOutcome(
        StepExecutionStatus finalStatus, RunStatus expected)
    {
        PlanEndsWith(finalStatus);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the executor ran the work; how it ended is the answer, not a failure");
        result.Value!.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData(StepExecutionStatus.Failed)]
    [InlineData(StepExecutionStatus.Cancelled)]
    [InlineData(StepExecutionStatus.Blocked)]
    public async Task AnOutcomeThatIsNotSuccessCarriesAReason(StepExecutionStatus finalStatus)
    {
        // A caller that asked for work and did not get it is owed something it can act on. "Blocked"
        // alone does not say a person has to approve something; "Cancelled" alone does not say the run
        // was stopped rather than never started.
        PlanEndsWith(finalStatus);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.Value!.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ThePlanIsRunUnderTheEnvelopeRecordedOnTheRun()
    {
        // The grant travels on the record precisely because the request that authorized it is long
        // gone. Re-resolving it here would let a later change to the caller's permissions retroactively
        // widen or narrow work already accepted.
        PlanEndsWith(StepExecutionStatus.Completed);
        PlanRunRequest? sent = null;
        _planRunExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PlanRunRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = new PlanId(Guid.NewGuid()),
                FinalStatus = StepExecutionStatus.Completed,
                TotalDuration = TimeSpan.Zero,
                StepStates = []
            }));

        var workflowId = Guid.NewGuid();
        var record = Record(workflowId.ToString());

        await BuildSut().ExecuteAsync(record, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.PlanId.Should().Be(new PlanId(workflowId));
        sent.Envelope.Should().BeSameAs(record.Envelope);
    }

    [Fact]
    public async Task ATargetThatIsNotAWorkflowId_FailsThatRunRatherThanThrowing()
    {
        // Unreachable through the HTTP surface, which parses the id before accepting the run. It is
        // still answered rather than thrown, so a malformed target fails one run instead of surfacing
        // as an unexpected dispatcher exception.
        var result = await BuildSut().ExecuteAsync(Record("not-a-guid"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _planRunExecutor.Verify(
            e => e.ExecuteAsync(It.IsAny<PlanRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnExecutorThatCouldNotRunThePlan_IsAFailedResultRatherThanAnOutcome()
    {
        // The distinction the return type exists for: "I ran it and it ended this way" is not the same
        // statement as "I could not run it", and only the second is a failed result.
        _planRunExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Fail("plan_run.execution_failed"));

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("plan_run.execution_failed");
    }
}
