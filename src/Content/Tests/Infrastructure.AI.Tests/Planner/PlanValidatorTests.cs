using Application.Core.Validation.Planner;
using Domain.AI.Planner;
using FluentAssertions;
using FluentValidation;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

public sealed class PlanValidatorTests
{
    private static PlanValidator CreateValidator(IServiceProvider? serviceProvider = null)
    {
        return new PlanValidator(
            serviceProvider ?? Mock.Of<IServiceProvider>(),
            NullLogger<PlanValidator>.Instance);
    }

    private static PlanValidator CreateValidatorWithConfigValidators()
    {
        var mock = new Mock<IServiceProvider>();
        mock.Setup(x => x.GetService(typeof(IValidator<LlmCallConfig>)))
            .Returns(new LlmCallConfigValidator());
        mock.Setup(x => x.GetService(typeof(IValidator<ToolUseConfig>)))
            .Returns(new ToolUseConfigValidator());
        mock.Setup(x => x.GetService(typeof(IValidator<HumanGateConfig>)))
            .Returns(new HumanGateConfigValidator());
        mock.Setup(x => x.GetService(typeof(IValidator<ConditionalBranchConfig>)))
            .Returns(new ConditionalBranchConfigValidator());
        mock.Setup(x => x.GetService(typeof(IValidator<SubPlanConfig>)))
            .Returns(new SubPlanConfigValidator());
        return new PlanValidator(mock.Object, NullLogger<PlanValidator>.Instance);
    }

    private static PlanStep CreateStep(
        PlanStepId? id = null,
        string? name = null,
        StepType type = StepType.LlmCall,
        StepConfiguration? config = null,
        TimeSpan? timeout = null)
    {
        return new PlanStep
        {
            Id = id ?? PlanStepId.New(),
            Name = name ?? "TestStep",
            Type = type,
            Configuration = config ?? new LlmCallConfig
            {
                SystemPrompt = "test",
                ModelDeploymentKey = "test-model"
            },
            RetryPolicy = new RetryPolicy(),
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };
    }

    private static PlanGraph CreateGraph(
        IReadOnlyList<PlanStep> steps,
        IReadOnlyList<PlanEdge>? edges = null,
        PlanId? id = null,
        PlanId? parentPlanId = null)
    {
        return new PlanGraph
        {
            Id = id ?? PlanId.New(),
            Name = "TestPlan",
            Steps = steps,
            Edges = edges ?? [],
            Configuration = new PlanConfiguration(),
            ParentPlanId = parentPlanId
        };
    }

    // --- Structural Validation ---

    [Fact]
    public async Task Validate_EmptyGraph_ReturnsFail()
    {
        var sut = CreateValidator();
        var graph = CreateGraph([]);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("no steps"));
    }

    [Fact]
    public async Task Validate_SingleStepGraph_ReturnsSuccess()
    {
        var sut = CreateValidator();
        var step = CreateStep(name: "OnlyStep");
        var graph = CreateGraph([step]);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_AcyclicGraph_ReturnsSuccess()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A");
        var b = CreateStep(name: "B");
        var c = CreateStep(name: "C");
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.ControlFlow),
            new(b.Id, c.Id, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b, c], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_CyclicGraph_ReturnsFail()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A");
        var b = CreateStep(name: "B");
        var c = CreateStep(name: "C");
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.ControlFlow),
            new(b.Id, c.Id, EdgeType.ControlFlow),
            new(c.Id, a.Id, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b, c], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("root nodes"));
    }

    [Fact]
    public async Task Validate_ZeroRootNodes_ReturnsFail()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A");
        var b = CreateStep(name: "B");
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.ControlFlow),
            new(b.Id, a.Id, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("root nodes"));
    }

    [Fact]
    public async Task Validate_UnreachableNode_ReturnsFail()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A");
        var b = CreateStep(name: "B");
        var c = CreateStep(name: "C");
        var d = CreateStep(name: "D");
        var e = CreateStep(name: "E");
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.ControlFlow),
            new(b.Id, c.Id, EdgeType.ControlFlow),
            new(d.Id, e.Id, EdgeType.ControlFlow),
            new(e.Id, d.Id, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b, c, d, e], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(err =>
            err.Contains("Cycle") && (err.Contains("D") || err.Contains("E")));
    }

    [Fact]
    public async Task Validate_EdgeReferencesNonexistentStep_ReturnsFail()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A");
        var b = CreateStep(name: "B");
        var phantomId = PlanStepId.New();
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.ControlFlow),
            new(a.Id, phantomId, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("non-existent"));
    }

    // --- Conditional Branch Validation ---

    [Fact]
    public async Task Validate_ConditionalBranch_MissingTrueEdge_ReturnsFail()
    {
        var sut = CreateValidator();
        var root = CreateStep(name: "Root");
        var trueTarget = CreateStep(name: "TrueTarget");
        var falseTarget = CreateStep(name: "FalseTarget");
        var branch = CreateStep(
            name: "Branch",
            type: StepType.ConditionalBranch,
            config: new ConditionalBranchConfig
            {
                ConditionExpression = "$.x > 0",
                TrueEdgeTargetId = trueTarget.Id,
                FalseEdgeTargetId = falseTarget.Id
            });
        var edges = new PlanEdge[]
        {
            new(root.Id, branch.Id, EdgeType.ControlFlow),
            new(branch.Id, falseTarget.Id, EdgeType.ConditionalFalse)
        };
        var graph = CreateGraph([root, branch, trueTarget, falseTarget], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ConditionalTrue"));
    }

    [Fact]
    public async Task Validate_ConditionalBranch_MissingFalseEdge_ReturnsFail()
    {
        var sut = CreateValidator();
        var root = CreateStep(name: "Root");
        var trueTarget = CreateStep(name: "TrueTarget");
        var falseTarget = CreateStep(name: "FalseTarget");
        var branch = CreateStep(
            name: "Branch",
            type: StepType.ConditionalBranch,
            config: new ConditionalBranchConfig
            {
                ConditionExpression = "$.x > 0",
                TrueEdgeTargetId = trueTarget.Id,
                FalseEdgeTargetId = falseTarget.Id
            });
        var edges = new PlanEdge[]
        {
            new(root.Id, branch.Id, EdgeType.ControlFlow),
            new(branch.Id, trueTarget.Id, EdgeType.ConditionalTrue)
        };
        var graph = CreateGraph([root, branch, trueTarget, falseTarget], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ConditionalFalse"));
    }

    [Fact]
    public async Task Validate_ConditionalBranch_BothEdgesPresent_ReturnsSuccess()
    {
        var sut = CreateValidator();
        var root = CreateStep(name: "Root");
        var trueTarget = CreateStep(name: "TrueTarget");
        var falseTarget = CreateStep(name: "FalseTarget");
        var branch = CreateStep(
            name: "Branch",
            type: StepType.ConditionalBranch,
            config: new ConditionalBranchConfig
            {
                ConditionExpression = "$.x > 0",
                TrueEdgeTargetId = trueTarget.Id,
                FalseEdgeTargetId = falseTarget.Id
            });
        var edges = new PlanEdge[]
        {
            new(root.Id, branch.Id, EdgeType.ControlFlow),
            new(branch.Id, trueTarget.Id, EdgeType.ConditionalTrue),
            new(branch.Id, falseTarget.Id, EdgeType.ConditionalFalse)
        };
        var graph = CreateGraph([root, branch, trueTarget, falseTarget], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsValid.Should().BeTrue();
    }

    // --- Sub-Plan Reference Validation ---

    [Fact]
    public async Task Validate_SelfReferencingSubPlan_ReturnsFail()
    {
        var sut = CreateValidator();
        var planId = PlanId.New();
        var step = CreateStep(
            name: "SelfRef",
            type: StepType.SubPlanInvocation,
            config: new SubPlanConfig { ChildPlanId = planId });
        var graph = CreateGraph([step], id: planId);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("self-reference"));
    }

    [Fact]
    public async Task Validate_AncestorReferencingSubPlan_ReturnsFail()
    {
        var sut = CreateValidator();
        var parentId = PlanId.New();
        var step = CreateStep(
            name: "AncestorRef",
            type: StepType.SubPlanInvocation,
            config: new SubPlanConfig { ChildPlanId = parentId });
        var graph = CreateGraph([step], parentPlanId: parentId);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("parent reference"));
    }

    // --- Step Configuration Validation (FluentValidation delegation) ---

    [Fact]
    public async Task Validate_LlmCallConfig_MissingDeploymentKey_ReturnsFail()
    {
        var sut = CreateValidatorWithConfigValidators();
        var step = CreateStep(
            name: "BadLlm",
            config: new LlmCallConfig
            {
                SystemPrompt = "test",
                ModelDeploymentKey = ""
            });
        var graph = CreateGraph([step]);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ModelDeploymentKey"));
    }

    [Fact]
    public async Task Validate_ToolUseConfig_EmptyToolName_ReturnsFail()
    {
        var sut = CreateValidatorWithConfigValidators();
        var step = CreateStep(
            name: "BadTool",
            type: StepType.ToolUse,
            config: new ToolUseConfig { ToolName = "" });
        var graph = CreateGraph([step]);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ToolName"));
    }

    [Fact]
    public async Task Validate_HumanGateConfig_InvalidApprovalStrategy_ReturnsFail()
    {
        var sut = CreateValidatorWithConfigValidators();
        var step = CreateStep(
            name: "BadGate",
            type: StepType.HumanGate,
            config: new HumanGateConfig
            {
                EscalationMessage = "Approve?",
                ApprovalStrategy = (ApprovalStrategy)999
            });
        var graph = CreateGraph([step]);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ApprovalStrategy"));
    }

    // --- Resource Estimation ---

    [Fact]
    public async Task Validate_ResourceEstimation_ReturnsCriticalPathDuration()
    {
        var sut = CreateValidator();
        var a = CreateStep(name: "A", timeout: TimeSpan.FromSeconds(10));
        var b = CreateStep(name: "B", timeout: TimeSpan.FromSeconds(20));
        var c = CreateStep(name: "C", timeout: TimeSpan.FromSeconds(5));
        var d = CreateStep(name: "D", timeout: TimeSpan.FromSeconds(10));
        var edges = new PlanEdge[]
        {
            new(a.Id, b.Id, EdgeType.DataFlow),
            new(a.Id, c.Id, EdgeType.DataFlow),
            new(b.Id, d.Id, EdgeType.ControlFlow),
            new(c.Id, d.Id, EdgeType.ControlFlow)
        };
        var graph = CreateGraph([a, b, c, d], edges);

        var result = await sut.ValidateAsync(graph, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.EstimatedCriticalPathDuration.Should().Be(TimeSpan.FromSeconds(40));
    }
}
