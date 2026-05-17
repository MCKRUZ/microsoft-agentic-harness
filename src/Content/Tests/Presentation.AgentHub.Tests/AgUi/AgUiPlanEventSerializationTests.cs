using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Serialization tests for planner-related AG-UI events.
/// Verifies correct JSON discriminator and property names on the wire.
/// </summary>
public sealed class AgUiPlanEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void PlanStartedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new PlanStartedEvent
        {
            PlanId = "plan-001",
            PlanName = "Analyze Repository",
            TotalSteps = 5,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_STARTED\"");
        json.Should().Contain("\"planId\":\"plan-001\"");
        json.Should().Contain("\"planName\":\"Analyze Repository\"");
        json.Should().Contain("\"totalSteps\":5");
    }

    [Fact]
    public void PlanStepStartedEvent_Serializes_WithStepFields()
    {
        var evt = new PlanStepStartedEvent
        {
            PlanId = "plan-001",
            StepId = "step-001",
            StepName = "Run Tests",
            StepType = "ToolUse",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_STEP_STARTED\"");
        json.Should().Contain("\"stepId\":\"step-001\"");
        json.Should().Contain("\"stepName\":\"Run Tests\"");
        json.Should().Contain("\"stepType\":\"ToolUse\"");
    }

    [Fact]
    public void PlanStepCompletedEvent_Serializes_WithStatusAndDuration()
    {
        var evt = new PlanStepCompletedEvent
        {
            PlanId = "plan-001",
            StepId = "step-001",
            Status = "Completed",
            DurationMs = 5500,
            OutputSummary = "All tests passed",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_STEP_COMPLETED\"");
        json.Should().Contain("\"status\":\"Completed\"");
        json.Should().Contain("\"durationMs\":5500");
        json.Should().Contain("\"outputSummary\":\"All tests passed\"");
    }

    [Fact]
    public void PlanStateUpdateEvent_Serializes_WithPatchOperations()
    {
        var evt = new PlanStateUpdateEvent
        {
            PlanId = "plan-001",
            Patch =
            [
                new JsonPatchOperation
                {
                    Op = "replace",
                    Path = "/steps/step-001/status",
                    Value = "Running",
                },
            ],
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_STATE_DELTA\"");
        json.Should().Contain("\"op\":\"replace\"");
        json.Should().Contain("\"path\":\"/steps/step-001/status\"");
    }

    [Fact]
    public void SandboxStatusEvent_Serializes_AsCustomType()
    {
        var evt = new SandboxStatusEvent
        {
            PlanId = "plan-001",
            StepId = "step-001",
            ToolName = "file_system",
            IsolationLevel = "Process",
            MemoryUsedBytes = 1048576,
            CpuTimeMs = 2500,
            AttestationHash = "abc123",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"SANDBOX_STATUS\"");
        json.Should().Contain("\"toolName\":\"file_system\"");
        json.Should().Contain("\"isolationLevel\":\"Process\"");
        json.Should().Contain("\"memoryUsedBytes\":1048576");
        json.Should().Contain("\"cpuTimeMs\":2500");
        json.Should().Contain("\"attestationHash\":\"abc123\"");
    }

    [Fact]
    public void PlanCompletedEvent_Serializes_WithSummary()
    {
        var evt = new PlanCompletedEvent
        {
            PlanId = "plan-001",
            TotalDurationMs = 150000,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_COMPLETED\"");
        json.Should().Contain("\"totalDurationMs\":150000");
    }

    [Fact]
    public void PlanFailedEvent_Serializes_WithErrorDetails()
    {
        var evt = new PlanFailedEvent
        {
            PlanId = "plan-001",
            FailedStepId = "step-003",
            ErrorMessage = "Connection timed out",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"PLAN_FAILED\"");
        json.Should().Contain("\"failedStepId\":\"step-003\"");
        json.Should().Contain("\"errorMessage\":\"Connection timed out\"");
    }

    [Fact]
    public void PlanStartedEvent_RoundTrip_DeserializesToCorrectSubtype()
    {
        var original = new PlanStartedEvent
        {
            PlanId = "round-trip-plan",
            PlanName = "Test Plan",
            TotalSteps = 3,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<PlanStartedEvent>();
        var result = (PlanStartedEvent)deserialized!;
        result.PlanId.Should().Be("round-trip-plan");
        result.PlanName.Should().Be("Test Plan");
        result.TotalSteps.Should().Be(3);
    }

    [Fact]
    public void PlanStepCompletedEvent_RoundTrip_DeserializesToCorrectSubtype()
    {
        var original = new PlanStepCompletedEvent
        {
            PlanId = "plan-rt",
            StepId = "step-rt",
            Status = "Failed",
            DurationMs = 1234,
            OutputSummary = "Error occurred",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<PlanStepCompletedEvent>();
        var result = (PlanStepCompletedEvent)deserialized!;
        result.Status.Should().Be("Failed");
        result.DurationMs.Should().Be(1234);
        result.OutputSummary.Should().Be("Error occurred");
    }

    [Fact]
    public void PlanFailedEvent_RoundTrip_DeserializesToCorrectSubtype()
    {
        var original = new PlanFailedEvent
        {
            PlanId = "plan-rt",
            FailedStepId = "step-fail",
            ErrorMessage = "Timeout after 30s",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<PlanFailedEvent>();
        var result = (PlanFailedEvent)deserialized!;
        result.FailedStepId.Should().Be("step-fail");
        result.ErrorMessage.Should().Be("Timeout after 30s");
    }

    [Fact]
    public void PlanStepCompletedEvent_NullOutputSummary_OmitsField()
    {
        var evt = new PlanStepCompletedEvent
        {
            PlanId = "plan-001",
            StepId = "step-001",
            Status = "Completed",
            DurationMs = 100,
            OutputSummary = null,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().NotContain("\"outputSummary\"");
    }

    [Fact]
    public void SandboxStatusEvent_NullAttestationHash_OmitsField()
    {
        var evt = new SandboxStatusEvent
        {
            PlanId = "plan-001",
            StepId = "step-001",
            ToolName = "calculator",
            IsolationLevel = "None",
            MemoryUsedBytes = 0,
            CpuTimeMs = 0,
            AttestationHash = null,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().NotContain("\"attestationHash\"");
    }
}
