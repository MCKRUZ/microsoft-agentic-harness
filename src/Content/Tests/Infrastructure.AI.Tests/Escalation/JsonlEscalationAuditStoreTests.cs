using System.Text.Json;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="JsonlEscalationAuditStore"/>.
/// Validates append-only JSONL semantics, round-trip serialization with RecordType
/// discriminator, concurrent write safety, and history retrieval by escalation ID.
/// </summary>
public sealed class JsonlEscalationAuditStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"audit-store-tests-{Guid.NewGuid():N}");

    private readonly JsonlEscalationAuditStore _store;

    public JsonlEscalationAuditStoreTests()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Governance = new GovernanceConfig
                {
                    Escalation = new EscalationConfig { AuditStoragePath = _tempDir }
                }
            }
        };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        _store = new JsonlEscalationAuditStore(options, Mock.Of<ILogger<JsonlEscalationAuditStore>>());
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static EscalationRequest BuildRequest(Guid? escalationId = null) => new()
    {
        EscalationId = escalationId ?? Guid.NewGuid(),
        AgentId = "agent-1",
        ToolName = "delete_file",
        Arguments = new Dictionary<string, string> { ["path"] = "/tmp/test.txt" },
        Description = "Delete a temporary file",
        RiskLevel = RiskLevel.Medium,
        Priority = EscalationPriority.Blocking,
        Approvers = ["admin"],
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static ApproverDecision BuildDecision(string approver = "admin") => new()
    {
        ApproverName = approver,
        Verdict = ApproverVerdict.Approve,
        Reason = "Looks safe",
        RespondedAt = DateTimeOffset.UtcNow
    };

    private static EscalationOutcome BuildOutcome(Guid escalationId) => new()
    {
        EscalationId = escalationId,
        IsApproved = true,
        Decisions = [BuildDecision()],
        ResolutionType = EscalationResolutionType.Approved,
        ResolvedAt = DateTimeOffset.UtcNow
    };

    private static EscalationExecutionRecord BuildExecutionRecord(Guid escalationId) =>
        EscalationExecutionRecord.Succeeded(escalationId, DateTimeOffset.UtcNow, "agent-turn");

    [Fact]
    public async Task RecordRequestAsync_AppendsToFile()
    {
        var request = BuildRequest();

        await _store.RecordRequestAsync(request, CancellationToken.None);

        var history = await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None);
        history.Should().HaveCount(1);
        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
        history[0].EscalationId.Should().Be(request.EscalationId);
    }

    [Fact]
    public async Task RecordDecisionAsync_AppendsToFile()
    {
        var escalationId = Guid.NewGuid();
        var decision = BuildDecision();

        await _store.RecordDecisionAsync(escalationId, decision, CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        history.Should().HaveCount(1);
        history[0].RecordType.Should().Be(EscalationAuditRecordType.Decision);
        history[0].EscalationId.Should().Be(escalationId);
    }

    [Fact]
    public async Task RecordOutcomeAsync_AppendsToFile()
    {
        var escalationId = Guid.NewGuid();
        var outcome = BuildOutcome(escalationId);

        await _store.RecordOutcomeAsync(outcome, CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        history.Should().HaveCount(1);
        history[0].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsAllRecordsForEscalation()
    {
        var escalationId = Guid.NewGuid();
        var noiseId = Guid.NewGuid();

        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);
        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);
        await _store.RecordRequestAsync(BuildRequest(noiseId), CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);

        history.Should().HaveCount(3);
        history.Should().AllSatisfy(r => r.EscalationId.Should().Be(escalationId));
        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
        history[1].RecordType.Should().Be(EscalationAuditRecordType.Decision);
        history[2].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
    }

    [Fact]
    public async Task GetHistoryAsync_UnknownId_ReturnsEmpty()
    {
        await _store.RecordRequestAsync(BuildRequest(), CancellationToken.None);

        var history = await _store.GetHistoryAsync(Guid.NewGuid(), CancellationToken.None);

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestExecutionAsync_NoExecutionRecorded_ReturnsNull()
    {
        // #396: nothing has ever deserialized an execution record back out of the audit trail —
        // EscalationExecutionRecord's private, factory-only constructor blocks System.Text.Json
        // from rehydrating it without a [JsonConstructor] escape hatch.
        var result = await _store.GetLatestExecutionAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestExecutionAsync_AfterRecordExecutionAsync_ReturnsTheRecord()
    {
        var escalationId = Guid.NewGuid();
        var record = BuildExecutionRecord(escalationId);

        await _store.RecordExecutionAsync(record, CancellationToken.None);
        var result = await _store.GetLatestExecutionAsync(escalationId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.EscalationId.Should().Be(escalationId);
        result.Status.Should().Be(EscalationExecutionStatus.Succeeded);
        result.ReportedBy.Should().Be("agent-turn");
    }

    [Fact]
    public async Task ConcurrentWrites_NoCorruption()
    {
        var requests = Enumerable.Range(0, 20)
            .Select(_ => BuildRequest())
            .ToList();

        await Task.WhenAll(requests.Select(r =>
            _store.RecordRequestAsync(r, CancellationToken.None)));

        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Should().HaveCount(20);

        foreach (var request in requests)
        {
            var history = await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None);
            history.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task RecordType_Discriminator_DeserializesCorrectly()
    {
        var escalationId = Guid.NewGuid();

        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);
        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);

        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
        history[0].Payload.Should().Contain("delete_file");

        history[1].RecordType.Should().Be(EscalationAuditRecordType.Decision);
        history[1].Payload.Should().Contain("admin");

        history[2].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
        history[2].Payload.Should().Contain("Approved");
    }

    // ===== #321 durable-format migration: a record written before ApproverVerdict existed =====

    private static readonly JsonSerializerOptions LegacyWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ModernReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new ApproverDecisionJsonConverter()
        }
    };

    [Fact]
    public async Task GetHistoryAsync_LegacyDecisionRecordWithBooleanApproved_ReadsApproveVerdict_ChainStillVerifies()
    {
        // Hand-writes a decision record in the pre-#321 shape (a boolean "approved", no
        // "verdict") directly onto the chain using the SAME HashChainedJsonlWriter the store
        // uses internally, so this exercises the real tamper-evident format — only the payload
        // shape is old, not the chain framing.
        var escalationId = Guid.NewGuid();
        var respondedAt = DateTimeOffset.UtcNow;

        var legacyDecisionJson = JsonSerializer.Serialize(new
        {
            approver_name = "admin",
            approved = true,
            reason = "Looks safe",
            responded_at = respondedAt
        }, LegacyWriteOptions);

        var legacyAuditRecordJson = JsonSerializer.Serialize(new
        {
            record_type = "Decision",
            escalation_id = escalationId,
            timestamp = DateTimeOffset.UtcNow,
            payload = legacyDecisionJson
        }, LegacyWriteOptions);

        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
        using (var rawWriter = new HashChainedJsonlWriter(filePath, NullLogger.Instance))
        {
            var appended = await rawWriter.AppendAsync(legacyAuditRecordJson, CancellationToken.None);
            appended.IsSuccess.Should().BeTrue();
        }

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        history.Should().ContainSingle();
        var decision = JsonSerializer.Deserialize<ApproverDecision>(history[0].Payload, ModernReadOptions);
        decision!.Verdict.Should().Be(ApproverVerdict.Approve);

        // Appending a fresh, modern-shaped record onto the same chain and verifying end-to-end
        // proves the payload-shape change did not disturb the chain framing at the boundary
        // between the hand-written legacy line and normal store writes.
        await _store.RecordDecisionAsync(Guid.NewGuid(), BuildDecision(), CancellationToken.None);
        var verification = await _store.VerifyChainAsync(CancellationToken.None);
        verification.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryAsync_LegacyDecisionRecordWithApprovedFalse_ReadsDenyVerdict()
    {
        // Mutation control: the legacy-shape reader must not blanket-default to Approve — a
        // legacy "approved": false record must read as Deny.
        var escalationId = Guid.NewGuid();
        var respondedAt = DateTimeOffset.UtcNow;

        var legacyDecisionJson = JsonSerializer.Serialize(new
        {
            approver_name = "admin",
            approved = false,
            reason = "Not safe",
            responded_at = respondedAt
        }, LegacyWriteOptions);

        var legacyAuditRecordJson = JsonSerializer.Serialize(new
        {
            record_type = "Decision",
            escalation_id = escalationId,
            timestamp = DateTimeOffset.UtcNow,
            payload = legacyDecisionJson
        }, LegacyWriteOptions);

        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
        using (var rawWriter = new HashChainedJsonlWriter(filePath, NullLogger.Instance))
        {
            await rawWriter.AppendAsync(legacyAuditRecordJson, CancellationToken.None);
        }

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        var decision = JsonSerializer.Deserialize<ApproverDecision>(history[0].Payload, ModernReadOptions);

        decision!.Verdict.Should().Be(ApproverVerdict.Deny);
    }
}
