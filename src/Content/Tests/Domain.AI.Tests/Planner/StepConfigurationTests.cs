using System.Text.Json;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Xunit;

namespace Domain.AI.Tests.Planner;

public sealed class StepConfigurationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void StepConfiguration_JsonPolymorphic_RoundTripsAllFiveSubtypes()
    {
        StepConfiguration[] configs =
        [
            new LlmCallConfig
            {
                SystemPrompt = "You are a helpful assistant",
                ModelDeploymentKey = "gpt-4",
                Temperature = 0.5,
                MaxTokens = 2048
            },
            new ToolUseConfig
            {
                ToolName = "file_system",
                InputParameters = new Dictionary<string, object?> { ["path"] = "/tmp" },
                IsolationLevelOverride = SandboxIsolationLevel.Container
            },
            new HumanGateConfig
            {
                EscalationMessage = "Please approve",
                ApprovalStrategy = ApprovalStrategy.AnyOf
            },
            new ConditionalBranchConfig
            {
                ConditionExpression = "$.result == true",
                TrueEdgeTargetId = PlanStepId.New(),
                FalseEdgeTargetId = PlanStepId.New()
            },
            new SubPlanConfig
            {
                ChildPlanId = PlanId.New(),
                IsolateContext = true
            }
        ];

        foreach (var original in configs)
        {
            var json = JsonSerializer.Serialize<StepConfiguration>(original, JsonOptions);
            var deserialized = JsonSerializer.Deserialize<StepConfiguration>(json, JsonOptions);

            Assert.NotNull(deserialized);
            Assert.Equal(original.GetType(), deserialized.GetType());
        }
    }

    [Fact]
    public void StepConfiguration_Discriminator_PreservesTypeInfoThroughSerialization()
    {
        StepConfiguration config = new LlmCallConfig
        {
            SystemPrompt = "test",
            ModelDeploymentKey = "gpt-4"
        };

        var json = JsonSerializer.Serialize<StepConfiguration>(config, JsonOptions);

        Assert.Contains("\"type\":\"llm_call\"", json);
    }
}
