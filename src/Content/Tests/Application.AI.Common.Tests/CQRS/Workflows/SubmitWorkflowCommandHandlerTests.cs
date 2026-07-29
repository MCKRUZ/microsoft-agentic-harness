using Application.AI.Common.CQRS.Workflows.Submit;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Workflows;

/// <summary>
/// Tests for <see cref="SubmitWorkflowCommandHandler"/>: the disabled gate, child-workflow resolution
/// and its depth bound, persistence failure handling, and the step-name-to-identifier map a caller
/// needs to make sense of later status responses.
/// </summary>
public sealed class SubmitWorkflowCommandHandlerTests
{
    private readonly Mock<IPlanStateStore> _planStore = new();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private SubmitWorkflowCommandHandler BuildSut(bool enabled = true, int maxNestingDepth = 3)
    {
        var config = new AppConfig();
        config.AI.WorkflowSubmission.Enabled = enabled;
        config.AI.WorkflowSubmission.MaxSubPlanNestingDepth = maxNestingDepth;

        _planStore
            .Setup(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        return new SubmitWorkflowCommandHandler(
            _planStore.Object,
            new StaticOptionsMonitor<AppConfig>(config),
            NullLogger<SubmitWorkflowCommandHandler>.Instance);
    }

    private static WorkflowStep LlmStep(string name) => new()
    {
        Name = name,
        Type = StepType.LlmCall,
        Configuration = new LlmCallStepConfiguration { SystemPrompt = "p", ModelDeploymentKey = "k" }
    };

    private static WorkflowStep SubPlanStep(string name, Guid childId) => new()
    {
        Name = name,
        Type = StepType.SubPlanInvocation,
        Configuration = new SubPlanStepConfiguration { ChildWorkflowId = childId }
    };

    private static SubmitWorkflowCommand Command(params WorkflowStep[] steps) => new()
    {
        Definition = new WorkflowDefinition
        {
            Name = "wf",
            Steps = steps,
            Edges = []
        }
    };

    /// <summary>Builds a stored plan that itself invokes <paramref name="childId"/>, forming a chain.</summary>
    private static PlanGraph StoredPlan(PlanId id, Guid? childId = null) => new()
    {
        Id = id,
        Name = "stored",
        Steps =
        [
            new PlanStep
            {
                Id = PlanStepId.New(),
                Name = "step",
                Type = childId is null ? StepType.LlmCall : StepType.SubPlanInvocation,
                Configuration = childId is null
                    ? new LlmCallConfig { SystemPrompt = "p", ModelDeploymentKey = "k" }
                    : new SubPlanConfig { ChildPlanId = new PlanId(childId.Value) },
                RetryPolicy = new RetryPolicy()
            }
        ],
        Edges = [],
        Configuration = new PlanConfiguration()
    };

    private void SetupVisiblePlan(PlanId id, Guid? childId = null) => _planStore
        .Setup(s => s.LoadPlanAsync(id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<PlanGraph?>.Success(StoredPlan(id, childId)));

    [Fact]
    public async Task Handle_WhenSubmissionIsDisabled_RefusesAndStoresNothing()
    {
        var sut = BuildSut(enabled: false);

        var result = await sut.Handle(Command(LlmStep("only")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _planStore.Verify(
            s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidWorkflow_PersistsItAndReturnsTheStepNameToIdMap()
    {
        var sut = BuildSut();
        PlanGraph? persisted = null;
        _planStore
            .Setup(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .Callback<PlanGraph, CancellationToken>((plan, _) => persisted = plan)
            .ReturnsAsync(Result.Success());

        var result = await sut.Handle(Command(LlmStep("alpha"), LlmStep("beta")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        persisted.Should().NotBeNull();
        result.Value!.WorkflowId.Should().Be(persisted!.Id.Value);
        result.Value.Name.Should().Be("wf");

        // The map is the caller's only route from the names it authored to the ids that will appear in
        // status and progress responses, so it must name every step and match what was stored.
        result.Value.StepIds.Should().HaveCount(2);
        foreach (var step in persisted.Steps)
            result.Value.StepIds[step.Name].Should().Be(step.Id.Value);
    }

    [Fact]
    public async Task Handle_WhenPersistenceFails_ReportsAGenericFailureWithoutStoreDetail()
    {
        var sut = BuildSut();
        _planStore
            .Setup(s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Fail("SQLITE_BUSY at /var/data/harness.db"));

        var result = await sut.Handle(Command(LlmStep("only")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        // The store's own message can name an internal path; the caller gets a stable, safe one and
        // the detail goes to the log.
        result.Errors.Should().ContainSingle().Which.Should().Be("The workflow could not be stored.");
    }

    [Fact]
    public async Task Handle_ChildWorkflowThatIsMissingOrNotVisible_IsRejectedBeforePersisting()
    {
        var sut = BuildSut();
        var childId = Guid.NewGuid();

        // LoadPlanAsync is scope-filtered, so another owner's plan and a non-existent one are the same
        // null. The caller learns its reference is unusable, not whether someone else holds that id.
        _planStore
            .Setup(s => s.LoadPlanAsync(new PlanId(childId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(null));

        var result = await sut.Handle(Command(SubPlanStep("child", childId)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().ContainMatch($"*{childId}*not available to this caller*");
        _planStore.Verify(
            s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ChildWorkflowThatResolves_IsAccepted()
    {
        var sut = BuildSut();
        var childId = Guid.NewGuid();
        SetupVisiblePlan(new PlanId(childId));

        var result = await sut.Handle(Command(SubPlanStep("child", childId)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ChainDeeperThanTheCap_IsRejectedAtAdmission()
    {
        // depth 1 -> depth 2 -> depth 3, against a cap of 2. Rejected here rather than tripping
        // PlanConfiguration.MaxSubPlanDepth mid-run, after the earlier steps have spent real calls.
        var sut = BuildSut(maxNestingDepth: 2);
        var level1 = Guid.NewGuid();
        var level2 = Guid.NewGuid();
        var level3 = Guid.NewGuid();

        SetupVisiblePlan(new PlanId(level1), level2);
        SetupVisiblePlan(new PlanId(level2), level3);
        SetupVisiblePlan(new PlanId(level3));

        var result = await sut.Handle(Command(SubPlanStep("child", level1)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*deeper than the host's limit of 2*");
        _planStore.Verify(
            s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ChainExactlyAtTheCap_IsAccepted()
    {
        var sut = BuildSut(maxNestingDepth: 2);
        var level1 = Guid.NewGuid();
        var level2 = Guid.NewGuid();

        SetupVisiblePlan(new PlanId(level1), level2);
        SetupVisiblePlan(new PlanId(level2));

        var result = await sut.Handle(Command(SubPlanStep("child", level1)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CyclicChildChain_TerminatesRatherThanWalkingForever()
    {
        // Structural validity is PlanValidator's job; all this walk owes is termination. A plan that
        // references itself would otherwise loop until the depth cap, or forever if the cap moved.
        var sut = BuildSut(maxNestingDepth: 5);
        var selfReferential = Guid.NewGuid();
        SetupVisiblePlan(new PlanId(selfReferential), selfReferential);

        var result = await sut.Handle(Command(SubPlanStep("child", selfReferential)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _planStore.Verify(
            s => s.LoadPlanAsync(new PlanId(selfReferential), It.IsAny<CancellationToken>()), Times.Once);
    }
}
