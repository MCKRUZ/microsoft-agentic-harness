using Domain.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class ConditionalBranchStepExecutorTests
{
    private readonly ConditionalBranchStepExecutor _sut = new(NullLogger<ConditionalBranchStepExecutor>.Instance);

    private static PlanStep CreateStep(ConditionalBranchConfig config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "test-branch",
        Type = StepType.ConditionalBranch,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "bad",
            Type = StepType.ConditionalBranch,
            Configuration = new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "y" },
            RetryPolicy = new RetryPolicy()
        };

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_TrueCondition_ReturnsActiveEdgeTarget()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "score > 5",
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"score": 10}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal("true", result.Output);
        Assert.Equal(trueTarget, result.ActiveEdgeTarget);
    }

    [Fact]
    public async Task ExecuteAsync_FalseCondition_ReturnsFalseEdgeTarget()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "score > 50",
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"score": 3}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal("false", result.Output);
        Assert.Equal(falseTarget, result.ActiveEdgeTarget);
    }

    [Fact]
    public async Task ExecuteAsync_BooleanVariable_EvaluatesCorrectly()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "isReady",
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"isReady": true}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal("true", result.Output);
        Assert.Equal(trueTarget, result.ActiveEdgeTarget);
    }

    [Fact]
    public async Task ExecuteAsync_AndOperator_EvaluatesBothConditions()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "x > 1 AND y > 1",
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"x": 5, "y": 0}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal("false", result.Output);
        Assert.Equal(falseTarget, result.ActiveEdgeTarget);
    }

    [Fact]
    public async Task ExecuteAsync_UnsafeExpression_RejectsWithFailed()
    {
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "System.IO.File.Exists(path)",
            TrueEdgeTargetId = new PlanStepId(Guid.NewGuid()),
            FalseEdgeTargetId = new PlanStepId(Guid.NewGuid())
        };
        var step = CreateStep(config);

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("unsafe", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_StringEquality_ComparesCorrectly()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = """status == "approved" """,
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"status": "approved"}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal("true", result.Output);
        Assert.Equal(trueTarget, result.ActiveEdgeTarget);
    }

    [Fact]
    public async Task ExecuteAsync_NotOperator_NegatesCondition()
    {
        var trueTarget = new PlanStepId(Guid.NewGuid());
        var falseTarget = new PlanStepId(Guid.NewGuid());
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "NOT isBlocked",
            TrueEdgeTargetId = trueTarget,
            FalseEdgeTargetId = falseTarget
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"isBlocked": false}""" };

        var result = await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.Equal("true", result.Output);
        Assert.Equal(trueTarget, result.ActiveEdgeTarget);
    }
}
