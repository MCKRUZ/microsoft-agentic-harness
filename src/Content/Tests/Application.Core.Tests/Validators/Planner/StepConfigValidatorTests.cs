using Application.Core.Validation.Planner;
using Domain.AI.Planner;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validators.Planner;

public sealed class StepConfigValidatorTests
{
    // --- LlmCallConfigValidator ---

    [Fact]
    public void LlmCallConfig_Valid_NoErrors()
    {
        var validator = new LlmCallConfigValidator();
        var config = new LlmCallConfig
        {
            SystemPrompt = "You are a helpful assistant.",
            ModelDeploymentKey = "gpt-4o"
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void LlmCallConfig_EmptyDeploymentKey_HasError()
    {
        var validator = new LlmCallConfigValidator();
        var config = new LlmCallConfig
        {
            SystemPrompt = "You are a helpful assistant.",
            ModelDeploymentKey = ""
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ModelDeploymentKey"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void LlmCallConfig_TemperatureOutOfRange_HasError(double temperature)
    {
        var validator = new LlmCallConfigValidator();
        var config = new LlmCallConfig
        {
            SystemPrompt = "test",
            ModelDeploymentKey = "gpt-4o",
            Temperature = temperature
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Temperature"));
    }

    [Fact]
    public void LlmCallConfig_MaxTokensZero_HasError()
    {
        var validator = new LlmCallConfigValidator();
        var config = new LlmCallConfig
        {
            SystemPrompt = "test",
            ModelDeploymentKey = "gpt-4o",
            MaxTokens = 0
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("MaxTokens"));
    }

    // --- ToolUseConfigValidator ---

    [Fact]
    public void ToolUseConfig_Valid_NoErrors()
    {
        var validator = new ToolUseConfigValidator();
        var config = new ToolUseConfig { ToolName = "file_system" };

        var result = validator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ToolUseConfig_EmptyToolName_HasError()
    {
        var validator = new ToolUseConfigValidator();
        var config = new ToolUseConfig { ToolName = "" };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ToolName"));
    }

    // --- HumanGateConfigValidator ---

    [Fact]
    public void HumanGateConfig_Valid_NoErrors()
    {
        var validator = new HumanGateConfigValidator();
        var config = new HumanGateConfig
        {
            EscalationMessage = "Please approve this action.",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void HumanGateConfig_EmptyMessage_HasError()
    {
        var validator = new HumanGateConfigValidator();
        var config = new HumanGateConfig
        {
            EscalationMessage = "",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("EscalationMessage"));
    }

    [Fact]
    public void HumanGateConfig_InvalidStrategy_HasError()
    {
        var validator = new HumanGateConfigValidator();
        var config = new HumanGateConfig
        {
            EscalationMessage = "Approve?",
            ApprovalStrategy = (ApprovalStrategy)999
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ApprovalStrategy"));
    }

    [Fact]
    public void HumanGateConfig_TimeoutNegative_HasError()
    {
        var validator = new HumanGateConfigValidator();
        var config = new HumanGateConfig
        {
            EscalationMessage = "Approve?",
            ApprovalStrategy = ApprovalStrategy.AllOf,
            Timeout = TimeSpan.FromSeconds(-1)
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Timeout"));
    }

    // --- ConditionalBranchConfigValidator ---

    [Fact]
    public void ConditionalBranchConfig_Valid_NoErrors()
    {
        var validator = new ConditionalBranchConfigValidator();
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "$.result == 'approved'",
            TrueEdgeTargetId = PlanStepId.New(),
            FalseEdgeTargetId = PlanStepId.New()
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ConditionalBranchConfig_EmptyExpression_HasError()
    {
        var validator = new ConditionalBranchConfigValidator();
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "",
            TrueEdgeTargetId = PlanStepId.New(),
            FalseEdgeTargetId = PlanStepId.New()
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("ConditionExpression"));
    }

    [Fact]
    public void ConditionalBranchConfig_MissingTrueTarget_HasError()
    {
        var validator = new ConditionalBranchConfigValidator();
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "$.x > 0",
            TrueEdgeTargetId = new PlanStepId(Guid.Empty),
            FalseEdgeTargetId = PlanStepId.New()
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("TrueEdgeTargetId"));
    }

    [Fact]
    public void ConditionalBranchConfig_MissingFalseTarget_HasError()
    {
        var validator = new ConditionalBranchConfigValidator();
        var config = new ConditionalBranchConfig
        {
            ConditionExpression = "$.x > 0",
            TrueEdgeTargetId = PlanStepId.New(),
            FalseEdgeTargetId = new PlanStepId(Guid.Empty)
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("FalseEdgeTargetId"));
    }

    // --- SubPlanConfigValidator ---

    [Fact]
    public void SubPlanConfig_Valid_NoErrors()
    {
        var validator = new SubPlanConfigValidator();
        var config = new SubPlanConfig { ChildPlanId = PlanId.New() };

        var result = validator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void SubPlanConfig_BothNull_HasError()
    {
        var validator = new SubPlanConfigValidator();
        var config = new SubPlanConfig();

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("ChildPlanId") || e.ErrorMessage.Contains("InlinePlanDefinition"));
    }

    [Fact]
    public void SubPlanConfig_BothSet_HasError()
    {
        var validator = new SubPlanConfigValidator();
        var config = new SubPlanConfig
        {
            ChildPlanId = PlanId.New(),
            InlinePlanDefinition = new PlanGraph
            {
                Id = PlanId.New(),
                Name = "Inline",
                Steps = [],
                Edges = [],
                Configuration = new PlanConfiguration()
            }
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("not both"));
    }

    // --- Additional edge cases ---

    [Fact]
    public void LlmCallConfig_MaxTokensNegative_HasError()
    {
        var validator = new LlmCallConfigValidator();
        var config = new LlmCallConfig
        {
            SystemPrompt = "test",
            ModelDeploymentKey = "gpt-4o",
            MaxTokens = -1
        };

        var result = validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("MaxTokens"));
    }
}
