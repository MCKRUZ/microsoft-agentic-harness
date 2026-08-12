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

    // ===== #321 additive wire fields: revisionRound / priorRevisionInstructions / verdict =====

    [Fact]
    public void EscalationRequestedEvent_WithNullRevisionFields_OmitsThem()
    {
        // Mirrors EscalationRequestedEvent_WithNullOptionalFields_OmitsThem for the two new
        // fields: a request on its first round must produce byte-identical JSON to what a
        // pre-#321 client already expects.
        var evt = new EscalationRequestedEvent
        {
            EscalationId = "test-id",
            AgentId = "agent-1",
            ToolName = "tool-1",
            Description = "desc",
            Priority = "Blocking",
            Approvers = ["approver@test.com"],
            TimeoutSeconds = 60,
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().NotContain("\"revisionRound\"");
        json.Should().NotContain("\"priorRevisionInstructions\"");
    }

    [Fact]
    public void EscalationRequestedEvent_PayloadMissingRevisionFields_StillDeserializes()
    {
        // The additivity contract itself: a payload shaped exactly like what a pre-#321 build
        // emits (no revisionRound, no priorRevisionInstructions keys at all) must still
        // deserialize — there is no version field on this wire, so "the client can ignore
        // fields it doesn't know about" is the only compatibility story, and it must hold in
        // both directions: an old payload reaching new code, not just new payload reaching old.
        const string legacyJson = """
            {"type":"ESCALATION_REQUESTED","escalationId":"legacy-id","agentId":"agent-1","toolName":"tool-1","description":"desc","priority":"Blocking","approvers":["approver@test.com"],"timeoutSeconds":60}
            """;

        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(legacyJson, JsonOptions);

        deserialized.Should().BeOfType<EscalationRequestedEvent>();
        var result = (EscalationRequestedEvent)deserialized!;
        result.EscalationId.Should().Be("legacy-id");
        result.RevisionRound.Should().BeNull();
        result.PriorRevisionInstructions.Should().BeNull();
    }

    [Fact]
    public void EscalationRequestedEvent_WithRevisionFields_RoundTrips()
    {
        var original = new EscalationRequestedEvent
        {
            EscalationId = "round-trip-revision",
            AgentId = "agent-1",
            ToolName = "tool-1",
            Description = "desc",
            Priority = "Blocking",
            Approvers = ["approver@test.com"],
            TimeoutSeconds = 60,
            RevisionRound = 2,
            PriorRevisionInstructions = "use the other path",
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationRequestedEvent>();
        var result = (EscalationRequestedEvent)deserialized!;
        result.RevisionRound.Should().Be(2);
        result.PriorRevisionInstructions.Should().Be("use the other path");
    }

    [Fact]
    public void EscalationResolvedEvent_DecisionPayloadMissingVerdict_StillDeserializes()
    {
        // Same additivity contract for AgUiApproverDecision.Verdict, added alongside the
        // pre-existing Approved bool.
        const string legacyJson = """
            {"type":"ESCALATION_RESOLVED","escalationId":"id","isApproved":false,"resolutionType":"Denied","resolvedAt":"2026-05-08T14:30:00+00:00","decisions":[{"approverName":"admin@company.com","approved":false,"reason":"Too risky"}]}
            """;

        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(legacyJson, JsonOptions);

        deserialized.Should().BeOfType<EscalationResolvedEvent>();
        var result = (EscalationResolvedEvent)deserialized!;
        result.Decisions.Should().ContainSingle();
        result.Decisions![0].Verdict.Should().BeNull();
        result.Decisions[0].Approved.Should().BeFalse();
    }

    [Fact]
    public void EscalationResolvedEvent_RevisedResolution_RoundTripsWithVerdict()
    {
        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
        var original = new EscalationResolvedEvent
        {
            EscalationId = "revised-id",
            IsApproved = false, // #321 asymmetry: Revised is not-approved
            ResolutionType = "Revised",
            ResolvedAt = resolvedAt,
            Decisions =
            [
                new AgUiApproverDecision
                {
                    ApproverName = "admin@company.com",
                    Approved = false,
                    Verdict = "Revise",
                    Reason = "use the other path",
                },
            ],
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationResolvedEvent>();
        var result = (EscalationResolvedEvent)deserialized!;
        result.IsApproved.Should().BeFalse();
        result.ResolutionType.Should().Be("Revised");
        result.Decisions![0].Verdict.Should().Be("Revise");
        result.Decisions[0].Approved.Should().BeFalse();
    }

    [Fact]
    public void EscalationResolvedEvent_RevisedDecision_CarriesInstructionsOverThePushChannel()
    {
        // Code-review finding: ApproverDecisionSummary (the REST DTO) got both Verdict and
        // Instructions in this PR, but AgUiApproverDecision (the SignalR push DTO) only got
        // Verdict — a dashboard client relying on the push channel alone could see that a
        // revision was requested but never the reviewer's actual words.
        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
        var original = new EscalationResolvedEvent
        {
            EscalationId = "revised-with-instructions",
            IsApproved = false,
            ResolutionType = "Revised",
            ResolvedAt = resolvedAt,
            Decisions =
            [
                new AgUiApproverDecision
                {
                    ApproverName = "admin@company.com",
                    Approved = false,
                    Verdict = "Revise",
                    Instructions = "use the read-only endpoint instead",
                },
            ],
        };

        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);

        deserialized.Should().BeOfType<EscalationResolvedEvent>();
        var result = (EscalationResolvedEvent)deserialized!;
        result.Decisions![0].Instructions.Should().Be("use the read-only endpoint instead");
    }

    [Fact]
    public void EscalationResolvedEvent_DecisionPayloadMissingInstructions_StillDeserializes()
    {
        // Additivity contract for the new field itself.
        const string legacyJson = """
            {"type":"ESCALATION_RESOLVED","escalationId":"id","isApproved":false,"resolutionType":"Denied","resolvedAt":"2026-05-08T14:30:00+00:00","decisions":[{"approverName":"admin@company.com","approved":false,"reason":"Too risky"}]}
            """;

        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(legacyJson, JsonOptions);

        deserialized.Should().BeOfType<EscalationResolvedEvent>();
        var result = (EscalationResolvedEvent)deserialized!;
        result.Decisions![0].Instructions.Should().BeNull();
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
