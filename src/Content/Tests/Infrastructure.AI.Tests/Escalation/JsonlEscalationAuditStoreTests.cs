using Infrastructure.AI.Tests.Audit;
using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Telemetry.Redaction;
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
        _store = new JsonlEscalationAuditStore(
            options,
            TimeProvider.System,
            Mock.Of<ILogger<JsonlEscalationAuditStore>>(),
            AuditPseudonyms.Create(),
            new DefaultContentRedactionFilter());
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

    private string DayFolder => Path.Combine(_tempDir, "escalations");

    private string[] Held() => Directory.Exists(_store.PendingLocation)
        ? Directory.GetFiles(_store.PendingLocation, "*.pending")
        : [];

    private void BreakTheTrail()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(DayFolder, "not a folder");
    }

    [Fact]
    public async Task RecordOutcomeAsync_FailedThenReDrivenByReconciliation_LeavesExactlyOneOutcomeAfterReplay()
    {
        // The escalation service parks a resolution whose outcome append failed and re-drives the same outcome until it is
        // written (DefaultEscalationService.Durability). Holding the failed append as well would write it a second time.
        var outcome = BuildOutcome(Guid.NewGuid());
        BreakTheTrail();
        var failed = () => _store.RecordOutcomeAsync(outcome, CancellationToken.None);
        await failed.Should().ThrowAsync<IOException>("the resolution stays parked, awaiting reconciliation");
        File.Delete(DayFolder);

        await _store.RecordOutcomeAsync(outcome, CancellationToken.None); // The reconciler's re-drive.
        await _store.ReplayPendingAsync(CancellationToken.None);

        var history = await _store.GetHistoryAsync(outcome.EscalationId, CancellationToken.None);
        history.Count(r => r.RecordType == EscalationAuditRecordType.Outcome).Should().Be(1);
    }

    public static TheoryData<EscalationAuditRecordType, bool> HeldWhenTheAppendFails => new()
    {
        // Refused or re-driven by the escalation service itself.
        { EscalationAuditRecordType.Request, false },
        { EscalationAuditRecordType.Decision, false },
        { EscalationAuditRecordType.Outcome, false },
        // Already ran, and its reporter carries on: nothing else would write it.
        { EscalationAuditRecordType.Execution, true },
    };

    [Theory]
    [MemberData(nameof(HeldWhenTheAppendFails))]
    public async Task EachRecordType_TheAppendFails_IsHeldOnlyWhenNothingElseWouldWriteIt(
        EscalationAuditRecordType recordType, bool held)
    {
        var escalationId = Guid.NewGuid();
        BreakTheTrail();

        var append = recordType switch
        {
            EscalationAuditRecordType.Request => () => _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None),
            EscalationAuditRecordType.Decision => () => _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None),
            EscalationAuditRecordType.Outcome => () => _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None),
            _ => (Func<Task>)(() => _store.RecordExecutionAsync(BuildExecutionRecord(escalationId), CancellationToken.None)),
        };
        await append.Should().ThrowAsync<IOException>();

        Held().Should().HaveCount(held ? 1 : 0);
    }

    [Fact]
    public async Task RecordExecutionAsync_FailedOnce_IsReplayedIntoTheTrail()
    {
        var execution = BuildExecutionRecord(Guid.NewGuid());
        BreakTheTrail();
        var failed = () => _store.RecordExecutionAsync(execution, CancellationToken.None);
        await failed.Should().ThrowAsync<IOException>();
        File.Delete(DayFolder);

        (await _store.ReplayPendingAsync(CancellationToken.None)).Written.Should().Be(1);

        (await _store.GetHistoryAsync(execution.EscalationId, CancellationToken.None))
            .Should().ContainSingle(r => r.RecordType == EscalationAuditRecordType.Execution);
    }

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
        var lines = await AuditTrailFiles.ReadAllLinesAsync(filePath);
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
        history[1].Payload.Should().Contain(AuditPseudonyms.Create().Person("admin"));

        history[2].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
        history[2].Payload.Should().Contain("Approved");
    }

    // ===== #903 audit minimisation: the trail keeps references, fingerprints and redacted text, never the raw values =====

    [Fact]
    public async Task RecordRequestAsync_RecordsPeopleAsReferencesAndArgumentsAsFingerprints()
    {
        // The trail is kept for good, so it must not hold the approver's name, the caller's id or what the tool was sent.
        var pseudonyms = AuditPseudonyms.Create();
        var request = BuildRequest() with
        {
            AgentId = "direct-invoke:user-oid-42",
            Arguments = new Dictionary<string, string> { ["path"] = "/home/alice/medical.txt" },
            Description = "Delete a file for alice@contoso.com",
            Approvers = ["bob@contoso.com"],
            PriorRevisionInstructions = "Ask carol@contoso.com first",
        };

        await _store.RecordRequestAsync(request, CancellationToken.None);

        var payload = (await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None)).Single().Payload;
        payload.Should().NotContain("alice").And.NotContain("bob@").And.NotContain("carol@").And.NotContain("user-oid-42");
        var recorded = JsonSerializer.Deserialize<EscalationRequest>(payload, ModernReadOptions)!;
        recorded.ToolName.Should().Be("delete_file", "which tool was asked for is the point of the record");
        recorded.Arguments.Should().ContainKey("path", "the argument names stay; only their values are fingerprinted")
            .WhoseValue.Should().Be(pseudonyms.Fingerprint("tool-argument", "/home/alice/medical.txt"));
        recorded.Approvers.Should().Equal(pseudonyms.Person("bob@contoso.com"));
        recorded.AgentId.Should().Be("direct-invoke:" + pseudonyms.Person("user-oid-42"));
        recorded.Description.Should().StartWith("Delete a file for ").And.Contain("[REDACTED");
    }

    [Fact]
    public async Task RecordRequestAsync_ValuesRepeatedInTheDescription_AreReplacedByTheirReferences()
    {
        // The router writes "Agent 'direct-invoke:{user id}' is attempting..." and a delegation repeats its task text in
        // the description. A raw value left there beside its reference would let any reader link the two without the
        // key. A user id is no email, so redaction alone would not catch it.
        var pseudonyms = AuditPseudonyms.Create();
        var request = BuildRequest() with
        {
            AgentId = "direct-invoke:8f14e45f-ceea-467a-9575-0f2a3b1c6d7e",
            Arguments = new Dictionary<string, string>
            {
                ["taskDescription"] = "Summarise the divorce papers for Dana Whitfield",
                ["flag"] = "true",
            },
            Description = "Agent 'direct-invoke:8f14e45f-ceea-467a-9575-0f2a3b1c6d7e' is attempting to call tool "
                + "'delegate'. Delegation blocked: Summarise the divorce papers for Dana Whitfield (true)",
        };

        await _store.RecordRequestAsync(request, CancellationToken.None);

        var payload = (await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None)).Single().Payload;
        payload.Should().NotContain("8f14e45f").And.NotContain("Dana Whitfield");
        var recorded = JsonSerializer.Deserialize<EscalationRequest>(payload, ModernReadOptions)!;
        recorded.Description.Should().Be(
            $"Agent '{recorded.AgentId}' is attempting to call tool 'delegate'. Delegation blocked: "
            + $"{pseudonyms.Fingerprint("tool-argument", "Summarise the divorce papers for Dana Whitfield")} (true)",
            "the references read in place of the values, and a short value such as 'true' is left alone");
        recorded.AgentId.Should().Be("direct-invoke:" + pseudonyms.Person("8f14e45f-ceea-467a-9575-0f2a3b1c6d7e"));
    }

    [Fact]
    public async Task RecordDecisionAndOutcomeAsync_RecordTheApproverAsAReferenceAndKeepTheRedactedReasoning()
    {
        // The key holder can still answer "did bob approve this?" by computing his reference; the reasoning stays because
        // it is the accountability the record exists for, with identifiers taken out.
        var pseudonyms = AuditPseudonyms.Create();
        var escalationId = Guid.NewGuid();
        var decision = BuildDecision("bob@contoso.com") with { Reason = "Fine, alice@contoso.com asked for it" };
        var outcome = BuildOutcome(escalationId) with
        {
            Decisions = [decision],
            Approvers = ["bob@contoso.com"],
            CancelledBy = "carol@contoso.com",
        };

        await _store.RecordDecisionAsync(escalationId, decision, CancellationToken.None);
        await _store.RecordOutcomeAsync(outcome, CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        history.Select(r => r.Payload).Should().AllSatisfy(p => p.Should().NotContain("@contoso.com"));
        var recordedDecision = JsonSerializer.Deserialize<ApproverDecision>(history[0].Payload, ModernReadOptions)!;
        recordedDecision.ApproverName.Should().Be(pseudonyms.Person("bob@contoso.com"));
        recordedDecision.Verdict.Should().Be(ApproverVerdict.Approve);
        recordedDecision.Reason.Should().StartWith("Fine, ").And.Contain("[REDACTED");
        var recordedOutcome = JsonSerializer.Deserialize<EscalationOutcome>(history[1].Payload, ModernReadOptions)!;
        recordedOutcome.Decisions.Single().ApproverName.Should().Be(pseudonyms.Person("bob@contoso.com"));
        recordedOutcome.Approvers.Should().Equal(pseudonyms.Person("bob@contoso.com"));
        recordedOutcome.CancelledBy.Should().Be(pseudonyms.Person("carol@contoso.com"));
    }

    [Fact]
    public async Task RawRecordsWrittenBeforeMinimisation_ReadAndVerifyAlongsideMinimisedOnes()
    {
        // Records written before #903 hold raw values and can never change. Readers skip a payload they cannot parse, so a
        // shape that dropped a field would vanish from every read and from the compliance report; this proves both shapes
        // still read, in one chain that still verifies.
        var escalationId = Guid.NewGuid();
        var rawRecordJson = JsonSerializer.Serialize(new
        {
            record_type = "Request",
            escalation_id = escalationId,
            timestamp = DateTimeOffset.UtcNow,
            payload = JsonSerializer.Serialize(BuildRequest(escalationId), ModernReadOptions),
        }, LegacyWriteOptions);
        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
        using (var rawWriter = new HashChainedJsonlWriter(filePath, NullLogger.Instance))
            (await rawWriter.AppendAsync(rawRecordJson, CancellationToken.None)).IsSuccess.Should().BeTrue();

        await _store.RecordDecisionAsync(escalationId, BuildDecision("bob"), CancellationToken.None);
        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);

        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
        history.Select(r => r.RecordType).Should().Equal(
            EscalationAuditRecordType.Request, EscalationAuditRecordType.Decision, EscalationAuditRecordType.Outcome);
        JsonSerializer.Deserialize<EscalationRequest>(history[0].Payload, ModernReadOptions)!.Approvers.Should().Equal("admin");
        JsonSerializer.Deserialize<ApproverDecision>(history[1].Payload, ModernReadOptions).Should().NotBeNull();
        JsonSerializer.Deserialize<EscalationOutcome>(history[2].Payload, ModernReadOptions).Should().NotBeNull();
        (await _store.VerifyChainAsync(checkpoint: null, CancellationToken.None)).IsValid.Should().BeTrue();
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
        var verification = await _store.VerifyChainAsync(checkpoint: null, CancellationToken.None);
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

    // ===== QueryAsync (#714): the broader read that GetHistoryAsync cannot do — no required
    // escalation id, so it can see across the whole trail =====

    [Fact]
    public async Task QueryAsync_NoFilters_ReturnsRecordsAcrossMultipleEscalations()
    {
        // This is the actual new capability over GetHistoryAsync, which is keyed to one
        // escalation id and can never return records spanning more than one.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await _store.RecordRequestAsync(BuildRequest(first), CancellationToken.None);
        await _store.RecordRequestAsync(BuildRequest(second), CancellationToken.None);

        var result = await _store.QueryAsync(new EscalationAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(r => r.EscalationId).Should().BeEquivalentTo([first, second]);
    }

    [Fact]
    public async Task QueryAsync_EscalationIdFilter_ReturnsOnlyThatEscalation()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _store.RecordRequestAsync(BuildRequest(target), CancellationToken.None);
        await _store.RecordRequestAsync(BuildRequest(other), CancellationToken.None);

        var result = await _store.QueryAsync(
            new EscalationAuditQuery { EscalationId = target }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.EscalationId == target);
    }

    [Fact]
    public async Task QueryAsync_TimeRangeFilter_ExcludesRecordsOutsideTheWindow()
    {
        var inWindowId = Guid.NewGuid();
        var beforeWindowId = Guid.NewGuid();

        // Hand-write a record with a timestamp before the query window, since RecordRequestAsync
        // always stamps DateTimeOffset.UtcNow and the test needs one deterministically outside
        // the window it is about to query.
        var beforeRecord = new EscalationAuditRecord
        {
            RecordType = EscalationAuditRecordType.Request,
            EscalationId = beforeWindowId,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-2),
            Payload = JsonSerializer.Serialize(BuildRequest(beforeWindowId), ModernReadOptions)
        };
        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
        using (var rawWriter = new HashChainedJsonlWriter(filePath, NullLogger.Instance))
        {
            var json = JsonSerializer.Serialize(beforeRecord, ModernReadOptions);
            (await rawWriter.AppendAsync(json, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await _store.RecordRequestAsync(BuildRequest(inWindowId), CancellationToken.None);

        var result = await _store.QueryAsync(new EscalationAuditQuery
        {
            Start = DateTimeOffset.UtcNow.AddHours(-1)
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.EscalationId == inWindowId);
    }

    [Fact]
    public async Task QueryAsync_RecordTypeFilter_ReturnsOnlyThatType()
    {
        var escalationId = Guid.NewGuid();
        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);
        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);

        var result = await _store.QueryAsync(
            new EscalationAuditQuery { RecordType = EscalationAuditRecordType.Decision },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.RecordType == EscalationAuditRecordType.Decision);
    }

    [Fact]
    public async Task QueryAsync_EmptyFile_ReturnsEmptySuccess()
    {
        var result = await _store.QueryAsync(new EscalationAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_CorruptLine_SkipsItAndReturnsTheRest()
    {
        var escalationId = Guid.NewGuid();
        var filePath = Path.Combine(_tempDir, "escalations.jsonl");

        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
        using (var rawWriter = new HashChainedJsonlWriter(filePath, NullLogger.Instance))
        {
            await rawWriter.AppendAsync("{ not valid json", CancellationToken.None);
        }
        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);

        var result = await _store.QueryAsync(new EscalationAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2,
            "the corrupt line is skipped with a warning, not surfaced as a failure");
    }

    [Fact]
    public async Task QueryAsync_OrdersResultsByTimestamp()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await _store.RecordRequestAsync(BuildRequest(first), CancellationToken.None);
        await _store.RecordRequestAsync(BuildRequest(second), CancellationToken.None);

        var result = await _store.QueryAsync(new EscalationAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeInAscendingOrder(r => r.Timestamp);
    }
}
