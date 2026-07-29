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
    private readonly Mock<IPlanValidator> _structuralValidator = new();

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

        _structuralValidator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult { IsValid = true }));

        return new SubmitWorkflowCommandHandler(
            _planStore.Object,
            _structuralValidator.Object,
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
    public async Task Handle_SelfReferentialChildChain_IsRejectedAsACycle()
    {
        // A sub-plan that invokes itself is unbounded recursion at run time. The walk names it rather
        // than terminating quietly, which an earlier shared visited-set version did — that version
        // stopped walking but reported the submission as fine.
        var sut = BuildSut(maxNestingDepth: 5);
        var selfReferential = Guid.NewGuid();
        SetupVisiblePlan(new PlanId(selfReferential), selfReferential);

        var result = await sut.Handle(Command(SubPlanStep("child", selfReferential)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*cycle*");
    }

    [Fact]
    public async Task Handle_DeepChainReachableOnlyByTheLongerRoute_IsStillRejected()
    {
        // The defect a shared visited set causes. Referencing both an ancestor and its descendant as
        // direct children marks the descendant seen at depth 1, so when the longer route reaches it at
        // depth 3 its subtree is never re-expanded and an over-deep chain is admitted. Depth is carried
        // per path precisely so the reported depth is the real one.
        var sut = BuildSut(maxNestingDepth: 3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        SetupVisiblePlan(new PlanId(a), b);
        SetupVisiblePlan(new PlanId(b), c);
        SetupVisiblePlan(new PlanId(c), d);
        SetupVisiblePlan(new PlanId(d));

        // Both 'a' (root of the long chain) and 'c' (a node deep inside it) referenced directly.
        var result = await sut.Handle(
            Command(SubPlanStep("long", a), SubPlanStep("shortcut", c)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*deeper than the host's limit of 3*");
    }

    [Fact]
    public async Task Handle_StructurallyInvalidPlan_IsRejectedBeforePersisting()
    {
        // The wire contract states PlanValidator enforces cycles, reachability and branch completeness
        // for submissions. That is only true if it runs here — otherwise a cyclic workflow is stored
        // happily and fails on first execution, telling the wrong person about the defect.
        // The real PlanValidator reports an invalid plan as a FAILED result of validation type, not as
        // a successful one carrying IsValid = false. An earlier version of this test mocked the latter
        // and passed while the handler turned every rejected plan into a 500; the integration test
        // caught it. Mock what the implementation actually returns.
        var sut = BuildSut();
        _structuralValidator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.ValidationFailure(
                ["Plan contains a cycle: alpha -> beta -> alpha."]));

        var result = await sut.Handle(Command(LlmStep("alpha"), LlmStep("beta")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().ContainMatch("*cycle*");
        _planStore.Verify(
            s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAChildReferenceCannotBeRead_ReportsAFaultNotAMalformedRequest()
    {
        // A transient store failure returned as a 400 tells the caller its request was wrong, so it
        // will not retry something that would have worked on the next attempt.
        var sut = BuildSut();
        var childId = Guid.NewGuid();
        _planStore
            .Setup(s => s.LoadPlanAsync(new PlanId(childId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Fail("database is locked"));

        var result = await sut.Handle(Command(SubPlanStep("child", childId)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().NotBe(ResultFailureType.Validation);
        result.Errors.Should().NotContainMatch("*database is locked*");
    }

    [Fact]
    public async Task Handle_ValidatorReportingInvalidViaTheResultValue_IsAlsoRejected()
    {
        // The interface permits Success(IsValid: false) even though the shipped implementation does
        // not use it, so an alternative validator must not be able to have its verdict ignored.
        var sut = BuildSut();
        _structuralValidator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult
            {
                IsValid = false,
                Errors = ["Unreachable step: orphan."]
            }));

        var result = await sut.Handle(Command(LlmStep("alpha")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        _planStore.Verify(
            s => s.SavePlanAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenValidationItselfFaults_ReportsAFaultNotAMalformedRequest()
    {
        var sut = BuildSut();
        _structuralValidator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Fail("validator dependency unavailable"));

        var result = await sut.Handle(Command(LlmStep("alpha")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().NotBe(ResultFailureType.Validation);
        result.Errors.Should().NotContainMatch("*dependency unavailable*");
    }

    [Fact]
    public async Task Handle_ChildGraphThatFansOutBeyondTheLookupBudget_IsRejected()
    {
        // Depth alone does not bound the walk: breadth multiplies at every level. A caller can admit
        // cheap plans that each name one child many times, then submit one request whose resolution
        // costs breadth^depth store reads. Rate limiting counts requests, not the work behind them.
        var sut = BuildSut(maxNestingDepth: 3);

        // 40 distinct children, each of which names 40 distinct grandchildren: 1 + 40 + 1600 lookups.
        var roots = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();
        var leaves = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        foreach (var leafId in leaves)
            SetupVisiblePlan(new PlanId(leafId));

        foreach (var rootId in roots)
            _planStore
                .Setup(s => s.LoadPlanAsync(new PlanId(rootId), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<PlanGraph?>.Success(PlanInvoking(new PlanId(rootId), leaves)));

        var steps = roots.Select((id, i) => SubPlanStep($"child{i}", id)).ToArray();

        var result = await sut.Handle(Command(steps), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*budget*");
    }

    [Fact]
    public async Task Handle_StoredChildNamingOneGrandchildManyTimes_CostsOneLookup()
    {
        // The dedupe under test is inside ChildReferencesOf — a STORED plan whose steps all invoke the
        // same grandchild describes one subtree, not fifty. (The root list is deduped separately, so a
        // test that repeats a reference in the SUBMITTED steps proves the wrong thing.) Deduping within
        // a node is not the shared-visited-set mistake: every copy inside one plan sits at the same
        // depth, so collapsing them cannot under-report depth.
        var sut = BuildSut();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        _planStore
            .Setup(s => s.LoadPlanAsync(new PlanId(childId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanGraph?>.Success(PlanInvoking(
                new PlanId(childId), [.. Enumerable.Repeat(grandchildId, 50)])));
        SetupVisiblePlan(new PlanId(grandchildId));

        var result = await sut.Handle(Command(SubPlanStep("child", childId)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _planStore.Verify(
            s => s.LoadPlanAsync(new PlanId(grandchildId), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A stored plan whose steps invoke each of <paramref name="childIds"/> once.</summary>
    private static PlanGraph PlanInvoking(PlanId id, IReadOnlyList<Guid> childIds) => new()
    {
        Id = id,
        Name = "fan-out",
        Steps = [.. childIds.Select((childId, i) => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"invoke{i}",
            Type = StepType.SubPlanInvocation,
            Configuration = new SubPlanConfig { ChildPlanId = new PlanId(childId) },
            RetryPolicy = new RetryPolicy()
        })],
        Edges = [],
        Configuration = new PlanConfiguration()
    };
}
