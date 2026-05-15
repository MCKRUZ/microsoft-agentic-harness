using Domain.AI.Planner;
using Xunit;

namespace Domain.AI.Tests.Planner;

public sealed class PlanGraphTests
{
    [Fact]
    public void PlanId_NewId_GeneratesUniqueGuids()
    {
        var id1 = PlanId.New();
        var id2 = PlanId.New();

        Assert.NotEqual(Guid.Empty, id1.Value);
        Assert.NotEqual(Guid.Empty, id2.Value);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void PlanId_Equality_SameGuidAreEqual()
    {
        var guid = Guid.NewGuid();
        var id1 = new PlanId(guid);
        var id2 = new PlanId(guid);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void PlanGraph_Steps_IsImmutableList()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "test",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig
            {
                SystemPrompt = "test",
                ModelDeploymentKey = "gpt-4"
            },
            RetryPolicy = new RetryPolicy()
        };

        var graph = new PlanGraph
        {
            Id = PlanId.New(),
            Name = "test plan",
            Steps = new[] { step },
            Edges = Array.Empty<PlanEdge>(),
            Configuration = new PlanConfiguration()
        };

        Assert.IsAssignableFrom<IReadOnlyList<PlanStep>>(graph.Steps);
        Assert.False(graph.Steps is List<PlanStep>);
    }

    [Fact]
    public void PlanStep_RequiredFields_CannotBeNull()
    {
        var step = new PlanStep
        {
            Id = PlanStepId.New(),
            Name = "test step",
            Type = StepType.ToolUse,
            Configuration = new ToolUseConfig { ToolName = "file_system" },
            RetryPolicy = new RetryPolicy()
        };

        Assert.NotNull(step.Name);
        Assert.NotNull(step.Configuration);
        Assert.NotNull(step.RetryPolicy);
    }

    [Fact]
    public void PlanConfiguration_MaxSubPlanDepth_DefaultsFive()
    {
        var config = new PlanConfiguration();

        Assert.Equal(5, config.MaxSubPlanDepth);
        Assert.Equal(TimeSpan.FromMinutes(30), config.PlanTimeout);
        Assert.Equal(10, config.MaxParallelSteps);
    }

    [Fact]
    public void EdgeType_ConditionalTrue_HasDistinctValue()
    {
        var values = Enum.GetValues<EdgeType>();

        Assert.Equal(4, values.Length);
        Assert.Contains(EdgeType.ConditionalTrue, values);
        Assert.Contains(EdgeType.ConditionalFalse, values);
        Assert.NotEqual((int)EdgeType.ConditionalTrue, (int)EdgeType.ConditionalFalse);
    }

    [Fact]
    public void StepExecutionStatus_AllStates_CoverExpectedTransitions()
    {
        var values = Enum.GetValues<StepExecutionStatus>();

        Assert.Equal(7, values.Length);
        Assert.Contains(StepExecutionStatus.Pending, values);
        Assert.Contains(StepExecutionStatus.Ready, values);
        Assert.Contains(StepExecutionStatus.Running, values);
        Assert.Contains(StepExecutionStatus.Completed, values);
        Assert.Contains(StepExecutionStatus.Failed, values);
        Assert.Contains(StepExecutionStatus.Skipped, values);
        Assert.Contains(StepExecutionStatus.Blocked, values);
    }
}
