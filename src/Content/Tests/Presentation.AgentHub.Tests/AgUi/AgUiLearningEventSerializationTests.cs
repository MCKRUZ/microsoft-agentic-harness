using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Serialization tests for learning-related AG-UI events.
/// Verifies correct JSON discriminator and property names on the wire.
/// </summary>
public class AgUiLearningEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void LearningCapturedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new LearningCapturedEvent
        {
            LearningId = "learning-001",
            Category = "FactualCorrection",
            AgentId = "research-agent",
            TeamId = "team-alpha",
            IsGlobal = false,
            SourceDescription = "User corrected date format",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"LEARNING_CAPTURED\"");
        json.Should().Contain("\"learningId\":\"learning-001\"");
        json.Should().Contain("\"category\":\"FactualCorrection\"");
        json.Should().Contain("\"agentId\":\"research-agent\"");
        json.Should().Contain("\"teamId\":\"team-alpha\"");
        json.Should().Contain("\"sourceDescription\":\"User corrected date format\"");
    }

    [Fact]
    public void LearningAppliedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new LearningAppliedEvent
        {
            LearningId = "learning-002",
            AgentId = "code-writer",
            Category = "StylePreference",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"LEARNING_APPLIED\"");
        json.Should().Contain("\"learningId\":\"learning-002\"");
        json.Should().Contain("\"agentId\":\"code-writer\"");
        json.Should().Contain("\"category\":\"StylePreference\"");
    }

    [Fact]
    public void LearningForgottenEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new LearningForgottenEvent
        {
            LearningId = "learning-003",
            Reason = "Superseded by newer learning",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"LEARNING_FORGOTTEN\"");
        json.Should().Contain("\"learningId\":\"learning-003\"");
        json.Should().Contain("\"reason\":\"Superseded by newer learning\"");
    }

    [Fact]
    public void LearningCapturedEvent_Deserializes_BackToCorrectType()
    {
        var original = new LearningCapturedEvent
        {
            LearningId = "round-trip-learning",
            Category = "ToolUsagePattern",
            AgentId = "agent-1",
            TeamId = "team-1",
            IsGlobal = false,
            SourceDescription = "Agent discovered efficient API call pattern",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<LearningCapturedEvent>();
        var result = (LearningCapturedEvent)deserialized!;
        result.LearningId.Should().Be("round-trip-learning");
        result.Category.Should().Be("ToolUsagePattern");
        result.AgentId.Should().Be("agent-1");
        result.TeamId.Should().Be("team-1");
        result.IsGlobal.Should().BeFalse();
        result.SourceDescription.Should().Be("Agent discovered efficient API call pattern");
    }

    [Fact]
    public void LearningAppliedEvent_Deserializes_BackToCorrectType()
    {
        var original = new LearningAppliedEvent
        {
            LearningId = "round-trip-applied",
            AgentId = "code-writer",
            Category = "StylePreference",
            ContextSummary = "Applied during code formatting",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<LearningAppliedEvent>();
        var result = (LearningAppliedEvent)deserialized!;
        result.LearningId.Should().Be("round-trip-applied");
        result.AgentId.Should().Be("code-writer");
        result.Category.Should().Be("StylePreference");
        result.ContextSummary.Should().Be("Applied during code formatting");
    }

    [Fact]
    public void LearningForgottenEvent_Deserializes_BackToCorrectType()
    {
        var original = new LearningForgottenEvent
        {
            LearningId = "round-trip-forgotten",
            Reason = "Superseded by newer learning",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<LearningForgottenEvent>();
        var result = (LearningForgottenEvent)deserialized!;
        result.LearningId.Should().Be("round-trip-forgotten");
        result.Reason.Should().Be("Superseded by newer learning");
    }

    [Fact]
    public void LearningAppliedEvent_WithNullOptionalFields_OmitsThem()
    {
        var evt = new LearningAppliedEvent
        {
            LearningId = "learning-004",
            AgentId = "agent-1",
            Category = "FactualCorrection",
            ContextSummary = null,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().NotContain("\"contextSummary\"");
    }
}
