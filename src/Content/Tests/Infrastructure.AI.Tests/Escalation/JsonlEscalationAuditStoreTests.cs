using Domain.AI.Escalation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Microsoft.Extensions.Logging;
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
        Approved = true,
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
}
