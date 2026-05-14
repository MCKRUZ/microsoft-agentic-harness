using System.Text.Json;
using Domain.AI.Escalation;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Serialization tests for escalation-related AG-UI events.
/// Verifies correct JSON discriminator and property names on the wire.
/// </summary>
public class AgUiEscalationEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void EscalationRequestedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new EscalationRequestedEvent
        {
            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            AgentId = "research-agent",
            ToolName = "file_system_write",
            Description = "Agent attempted to write to protected directory",
            Priority = "Critical",
            Approvers = ["admin@company.com", "security@company.com"],
            TimeoutSeconds = 300,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"ESCALATION_REQUESTED\"");
        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
        json.Should().Contain("\"agentId\":\"research-agent\"");
        json.Should().Contain("\"toolName\":\"file_system_write\"");
        json.Should().Contain("\"description\":\"Agent attempted to write to protected directory\"");
        json.Should().Contain("\"priority\":\"Critical\"");
        json.Should().Contain("\"timeoutSeconds\":300");
        json.Should().Contain("admin@company.com");
        json.Should().Contain("security@company.com");
    }

    [Fact]
    public void EscalationResolvedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
        var evt = new EscalationResolvedEvent
        {
            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            IsApproved = true,
            ResolutionType = "Approved",
            ResolvedAt = resolvedAt,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"ESCALATION_RESOLVED\"");
        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
        json.Should().Contain("\"isApproved\":true");
        json.Should().Contain("\"resolutionType\":\"Approved\"");
    }

    [Fact]
    public void EscalationExpiringEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new EscalationExpiringEvent
        {
            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            RemainingSeconds = 30,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"ESCALATION_EXPIRING\"");
        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
        json.Should().Contain("\"remainingSeconds\":30");
    }

    [Fact]
    public void EscalationRequestedEvent_WithNullOptionalFields_OmitsThem()
    {
        var evt = new EscalationRequestedEvent
        {
            EscalationId = "test-id",
            AgentId = "agent-1",
            ToolName = "tool-1",
            Description = "desc",
            Priority = "Blocking",
            Approvers = ["approver@test.com"],
            TimeoutSeconds = 60,
            Arguments = null,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().NotContain("\"arguments\"");
    }

    [Fact]
    public void EscalationResolvedEvent_Deserializes_BackToCorrectType()
    {
        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
        var original = new EscalationResolvedEvent
        {
            EscalationId = "round-trip-id",
            IsApproved = false,
            ResolutionType = "Denied",
            ResolvedAt = resolvedAt,
            Decisions =
            [
                new AgUiApproverDecision
                {
                    ApproverName = "admin@company.com",
                    Approved = false,
                    Reason = "Too risky",
                },
            ],
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationResolvedEvent>();
        var result = (EscalationResolvedEvent)deserialized!;
        result.EscalationId.Should().Be("round-trip-id");
        result.IsApproved.Should().BeFalse();
        result.ResolutionType.Should().Be("Denied");
        result.Decisions.Should().HaveCount(1);
        result.Decisions![0].ApproverName.Should().Be("admin@company.com");
        result.Decisions[0].Reason.Should().Be("Too risky");
    }

    [Fact]
    public void EscalationRequestedEvent_Deserializes_BackToCorrectType()
    {
        var original = new EscalationRequestedEvent
        {
            EscalationId = "round-trip-requested",
            AgentId = "agent-1",
            ToolName = "tool-1",
            Description = "test description",
            Priority = "Critical",
            Approvers = ["approver@test.com"],
            TimeoutSeconds = 120,
            Arguments = new Dictionary<string, string> { ["key"] = "value" },
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationRequestedEvent>();
        var result = (EscalationRequestedEvent)deserialized!;
        result.EscalationId.Should().Be("round-trip-requested");
        result.AgentId.Should().Be("agent-1");
        result.Priority.Should().Be("Critical");
        result.TimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void EscalationExpiringEvent_Deserializes_BackToCorrectType()
    {
        var original = new EscalationExpiringEvent
        {
            EscalationId = "round-trip-expiring",
            RemainingSeconds = 42,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationExpiringEvent>();
        var result = (EscalationExpiringEvent)deserialized!;
        result.EscalationId.Should().Be("round-trip-expiring");
        result.RemainingSeconds.Should().Be(42);
    }
}
