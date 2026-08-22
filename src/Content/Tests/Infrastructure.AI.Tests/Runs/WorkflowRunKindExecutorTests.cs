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
    public async Task ThePlanRunRequest_CarriesTheRunsOwnJobIdAsItsRunId_NotTheSharedWorkflowId()
    {
        // The exact scoping bug this guards against: PlanRunRequest.ConversationId is left null so
        // every run of one workflow shares a single token-budget key by design — but a call-once
        // claim keyed on that SAME shared value would mean the first call to a call-once tool, in
        // any run, by any caller, permanently refuses every future run of this workflow. RunId must
        // be this run's own JobId, independent of the shared PlanId/ConversationId scope.
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

        var record = Record();

        await BuildSut().ExecuteAsync(record, CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.RunId.Should().Be(record.JobId);
        sent.ConversationId.Should().BeNull("the run scope still derives from the plan id for budget purposes");
        sent.RunId.Should().NotBe(sent.PlanId.Value.ToString());
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
    public async Task AParkedPlan_NamesTheDecisionsThatCouldReleaseIt()
    {
        // Without these ids the park is unrecoverable. Nothing else on the run says what it is waiting
        // for, so the resume check has nothing to ask about and the run sits until the host's
        // parked-run ceiling fails it — an approval turning into a silent expiry days later.
        var gateEscalation = Guid.NewGuid();

        PlanEndsWithStates(StepExecutionStatus.Blocked,
        [
            State(StepExecutionStatus.Completed, output: "the real output of a finished step"),
            State(StepExecutionStatus.Blocked, output: EscalationStepOutput.Serialize(gateEscalation))
        ]);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.Value!.AwaitingEscalationIds.Should().Equal(gateEscalation);
    }

    [Fact]
    public async Task TwoGatesReachedAtOnce_AreBothNamed()
    {
        // Parallel branches can both reach a gate before the plan drains. Reporting only the first
        // would tie the run's resume to one approver: if the other answered first, nothing would
        // notice, and the run would wait on a decision that had already been made.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        PlanEndsWithStates(StepExecutionStatus.Blocked,
        [
            State(StepExecutionStatus.Blocked, output: EscalationStepOutput.Serialize(first)),
            State(StepExecutionStatus.Blocked, output: EscalationStepOutput.Serialize(second))
        ]);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.Value!.AwaitingEscalationIds.Should().BeEquivalentTo([first, second]);
    }

    [Fact]
    public async Task AStepBlockedWithNoReadableEscalation_StillParksRatherThanFailing()
    {
        // The run is genuinely blocked and saying otherwise would be worse than saying nothing: a
        // caller told the work failed would retry it, against a plan whose gate is still pending. The
        // honest report is "parked, with nothing named" — which the ceiling eventually resolves.
        PlanEndsWithStates(StepExecutionStatus.Blocked,
        [
            State(StepExecutionStatus.Blocked, output: "{\"somethingElse\":\"not an escalation\"}")
        ]);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(RunStatus.Blocked);
        result.Value.AwaitingEscalationIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEscalationOnAStepThatIsNotBlocked_IsNotWaitedOn()
    {
        // A gate that was approved on an earlier resume keeps its escalation reference in history.
        // Treating it as still-awaited would resume the run on a verdict it has already acted on,
        // every single pass, for as long as the run lives.
        PlanEndsWithStates(StepExecutionStatus.Blocked,
        [
            State(StepExecutionStatus.Completed, output: EscalationStepOutput.Serialize(Guid.NewGuid())),
            State(StepExecutionStatus.Blocked, output: null)
        ]);

        var result = await BuildSut().ExecuteAsync(Record(), CancellationToken.None);

        result.Value!.AwaitingEscalationIds.Should().BeEmpty();
    }

    private static StepExecutionState State(StepExecutionStatus status, string? output) => new()
    {
        StepId = new PlanStepId(Guid.NewGuid()),
        Status = status,
        Output = output
    };

    private void PlanEndsWithStates(
        StepExecutionStatus finalStatus, IReadOnlyList<StepExecutionState> stepStates) =>
        _planRunExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<PlanRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanExecutionSummary>.Success(new PlanExecutionSummary
            {
                PlanId = new PlanId(Guid.NewGuid()),
                FinalStatus = finalStatus,
                TotalDuration = TimeSpan.FromSeconds(1),
                StepStates = stepStates
            }));

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
