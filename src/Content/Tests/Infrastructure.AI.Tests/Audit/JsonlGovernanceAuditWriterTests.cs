using System.Diagnostics.Metrics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Telemetry.Redaction;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// Tests for <see cref="JsonlGovernanceAuditWriter"/> — the durable, hash-chained replacement for
/// the deleted <c>AgtAuditAdapter</c> (#407). Covers the shape <c>AgtAuditAdapterTests</c> used to
/// (log/count/verify) plus the durability guarantee that is the whole point of this class: a fresh
/// writer instance over the same file resumes the chain correctly after a simulated restart.
/// </summary>
public sealed class JsonlGovernanceAuditWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _expectedFile;

    public JsonlGovernanceAuditWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"governance-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _expectedFile = Path.Combine(_tempDir, "governance.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // One instance for the writer and the test, so the references a test builds are ones the writer issued.
    private readonly HmacAuditPseudonymizer _pseudonyms = AuditPseudonyms.Create();

    private JsonlGovernanceAuditWriter NewWriter(string? storagePathOverride = null) =>
        new(
            Monitor(storagePathOverride ?? _tempDir),
            TimeProvider.System,
            NullLogger<JsonlGovernanceAuditWriter>.Instance,
            _pseudonyms,
            new DefaultContentRedactionFilter());

    private static IOptionsMonitor<AppConfig> Monitor(string storagePath)
    {
        var cfg = new AppConfig
        {
            AI = new()
            {
                Governance = new() { AuditStoragePath = storagePath }
            }
        };
        return new TestConfig.StaticOptionsMonitor<AppConfig>(cfg);
    }

    [Fact]
    public void Log_WritesOneLinePerCall()
    {
        using var sut = NewWriter();

        sut.Log("agent-1", "run_tests", "allowed");
        sut.Log("agent-1", "iac_plan", "denied");

        AuditTrailFiles.ReadAllLines(_expectedFile).Should().HaveCount(2);
    }

    [Fact]
    public void Log_DirectInvocation_RecordsTheCallerAsTheAuditReference()
    {
        // A direct invocation runs as "direct-invoke:{user id}"; the trail is kept for good, so it records the caller as a
        // reference the key holder can recompute, not the user id.
        using var sut = NewWriter();

        sut.Log("direct-invoke:user-oid-42", "run_tests", "allowed");
        sut.Log("agent-1", "run_tests", "allowed");

        var lines = AuditTrailFiles.ReadAllLines(_expectedFile);
        lines[0].Should().NotContain("user-oid-42")
            .And.Contain($"\"agent_id\":\"direct-invoke:{AuditPseudonyms.Create().Person("user-oid-42")}\"");
        lines[1].Should().Contain("\"agent_id\":\"agent-1\"", "a configured agent's name is not personal data");
    }

    [Fact]
    public async Task GetRecordsAsync_ByDirectInvocationAgentId_FindsRecordsWrittenBeforeAndAfterPseudonymisation()
    {
        // Records written before #903 hold the raw "direct-invoke:{user id}"; later ones hold the reference. A query by the
        // raw id must find both, or it silently drops everything written since.
        using var sut = NewWriter();
        sut.Log("direct-invoke:user-oid-42", "run_tests", "allowed");
        var legacyLine = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            agent_id = "direct-invoke:user-oid-42",
            action = "run_tests",
            decision = "allowed",
        });
        using (var raw = new HashChainedJsonlWriter(_expectedFile, NullLogger.Instance))
            (await raw.AppendAsync(legacyLine, CancellationToken.None)).IsSuccess.Should().BeTrue();
        sut.Log("direct-invoke:someone-else", "run_tests", "allowed");

        var result = await sut.GetRecordsAsync(
            new GovernanceAuditQuery { AgentId = "direct-invoke:user-oid-42" }, CancellationToken.None);

        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public void Log_FreeTextInTheActionAndDecision_IsRedactedWithoutTouchingAuditReferences()
    {
        // Producers fill these with free text (a delegation's model reasoning, a memory key). The trail is kept for good,
        // so it is redacted, but a reference already in it must survive: its hex is what the digit rules would take for
        // a phone number, and a mangled reference matches nobody.
        using var sut = NewWriter();
        var reference = _pseudonyms.Person("bob");

        sut.Log("agent-1", "memory_write:Person:dana@contoso.com", $"RequireApproval:Approved|By:{reference}|Reason:call +1 415 555 0100");

        var line = AuditTrailFiles.ReadAllLines(_expectedFile)[0];
        line.Should().NotContain("dana@contoso.com").And.NotContain("555 0100");
        line.Should().Contain($"By:{reference}|Reason:call [REDACTED");
    }

    [Fact]
    public void Log_MarkerCharactersInTheInput_CannotBeTurnedIntoAReference()
    {
        // The redaction step marks references with private-use characters. Text that already holds them must not be able to
        // claim a reference's place in the record.
        using var sut = NewWriter();
        var reference = _pseudonyms.Person("bob");

        sut.Log("agent-1", "run_tests", $"By:{reference}|Reason:\uE000a\uE001 call dana@contoso.com");

        var line = AuditTrailFiles.ReadAllLines(_expectedFile)[0];
        line.Should().Contain($"By:{reference}|Reason:a call [REDACTED", "the stray markers are dropped and the letter between them is plain text");
        line.Split(reference).Should().HaveCount(2, "the reference appears once, where the producer put it");
    }

    [Fact]
    public void Log_RedactionFilterFails_WritesThePlaceholderAndNeverThrows()
    {
        // A replaced or timed-out filter must not fail the decision being recorded, nor let the raw text through.
        using var sut = new JsonlGovernanceAuditWriter(
            Monitor(_tempDir),
            TimeProvider.System,
            NullLogger<JsonlGovernanceAuditWriter>.Instance,
            AuditPseudonyms.Create(),
            new ThrowingRedaction());

        var act = () => sut.Log("agent-1", "memory_write:Person:dana@contoso.com", "call +1 415 555 0100");

        act.Should().NotThrow();
        var line = AuditTrailFiles.ReadAllLines(_expectedFile)[0];
        line.Should().NotContain("dana").And.NotContain("555 0100");
        line.Should().Contain("\"action\":\"[REDACTION_FAILED]\"").And.Contain("\"decision\":\"[REDACTION_FAILED]\"");
    }

    [Fact]
    public void Log_OnlyReferencesThisProcessIssued_AreShieldedFromRedaction()
    {
        // Anything else shaped like a reference, even under the current key id, is ordinary text: the shield must not let
        // a chosen value slip past the filter.
        var pseudonyms = AuditPseudonyms.Create();
        var reference = pseudonyms.Person("bob");
        var foreign = $"ref:{pseudonyms.KeyId}:" + new string('a', 64);
        using var sut = new JsonlGovernanceAuditWriter(
            Monitor(_tempDir),
            TimeProvider.System,
            NullLogger<JsonlGovernanceAuditWriter>.Instance,
            pseudonyms,
            new RedactEverything());

        sut.Log("agent-1", "run_tests", $"{reference} {foreign}");

        var line = AuditTrailFiles.ReadAllLines(_expectedFile)[0];
        line.Should().Contain($"\"decision\":\"{reference} X\"");
    }

    private sealed class ThrowingRedaction : Application.AI.Common.Interfaces.Telemetry.IContentRedactionFilter
    {
        public string Redact(string? content, IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory> categories) =>
            throw new System.Text.RegularExpressions.RegexMatchTimeoutException("busy");
    }

    // Replaces every word except the shield's markers with "X", so whatever survives was shielded.
    private sealed class RedactEverything : Application.AI.Common.Interfaces.Telemetry.IContentRedactionFilter
    {
        public string Redact(string? content, IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory> categories) =>
            System.Text.RegularExpressions.Regex.Replace(
                content ?? string.Empty, "\uE000[a-z]+\uE001|[^ ]+", m => m.Value[0] == '\uE000' ? m.Value : "X");
    }

    [Fact]
    public void Log_IncludesAllExpectedFields()
    {
        using var sut = NewWriter();

        sut.Log("agent-1", "run_tests", "allowed");

        var line = AuditTrailFiles.ReadAllLines(_expectedFile)[0];
        line.Should().Contain("\"agent_id\":\"agent-1\"");
        line.Should().Contain("\"action\":\"run_tests\"");
        line.Should().Contain("\"decision\":\"allowed\"");
        line.Should().Contain("\"timestamp\"");
    }

    [Fact]
    public void EntryCount_InitiallyZero()
    {
        using var sut = NewWriter();

        sut.EntryCount.Should().Be(0);
    }

    [Fact]
    public void Log_MultipleEntries_TracksCorrectCount()
    {
        using var sut = NewWriter();

        sut.Log("agent-1", "run_tests", "allowed");
        sut.Log("agent-1", "run_lint", "allowed");
        sut.Log("agent-2", "iac_plan", "denied");

        sut.EntryCount.Should().Be(3);
    }

    [Fact]
    public void VerifyChainIntegrity_EmptyChain_ReturnsTrue()
    {
        using var sut = NewWriter();

        sut.VerifyChainIntegrity().Should().BeTrue();
    }

    [Fact]
    public void VerifyChainIntegrity_ValidChain_ReturnsTrue()
    {
        using var sut = NewWriter();
        sut.Log("agent-1", "run_tests", "allowed");
        sut.Log("agent-1", "run_lint", "allowed");

        sut.VerifyChainIntegrity().Should().BeTrue();
    }

    [Fact]
    public void VerifyChainIntegrity_TamperedRecord_ReturnsFalse()
    {
        using (var sut = NewWriter())
        {
            sut.Log("agent-1", "run_tests", "allowed");
        }

        var tampered = File.ReadAllText(AuditTrailFiles.OnlyDayFile(_expectedFile)).Replace("\"allowed\"", "\"denied\"");
        File.WriteAllText(AuditTrailFiles.OnlyDayFile(_expectedFile), tampered);

        using var reopened = NewWriter();
        reopened.VerifyChainIntegrity().Should().BeFalse(
            "altering a persisted record must break the hash chain");
    }

    [Fact]
    public void Log_SurvivesSimulatedRestart_NewInstanceResumesTheChain()
    {
        // The entire point of #407: the trail must not vanish when the process restarts.
        using (var first = NewWriter())
        {
            first.Log("agent-1", "run_tests", "allowed");
            first.Log("agent-1", "run_lint", "allowed");
        }

        using var second = NewWriter();
        second.Log("agent-2", "iac_plan", "denied");

        second.EntryCount.Should().Be(3, "a fresh writer instance over the same file must see the prior process's entries");
        second.VerifyChainIntegrity().Should().BeTrue(
            "the new entry must chain onto the last entry the prior instance wrote, not restart from genesis");
    }

    [Fact]
    public void Log_WriteFailure_DoesNotThrow()
    {
        // A file blocking the storage directory makes Directory.CreateDirectory throw IOException —
        // the class's documented "Log never throws" contract must hold regardless.
        var blockingFilePath = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blockingFilePath, "not a directory");
        using var sut = NewWriter(Path.Combine(blockingFilePath, "governance"));

        var act = () => sut.Log("agent-1", "run_tests", "allowed");

        act.Should().NotThrow("a governance audit write failure must degrade the trail, never the caller's decision");
    }

    [Fact]
    public void Log_WriteFailure_IncrementsAuditWriteFailuresMetric()
    {
        // A security-review finding on #407's follow-up: Log() never throws on a write failure, so
        // the AuditWriteFailures counter is the only non-log-line signal a failure ever produces.
        // The action tag is a per-test GUID so a listener on the process-wide shared meter can't pick
        // up a measurement from a concurrently-running test in this parallelized assembly.
        var action = $"probe-{Guid.NewGuid():N}";
        var blockingFilePath = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blockingFilePath, "not a directory");
        using var sut = NewWriter(Path.Combine(blockingFilePath, "governance"));
        var failures = CaptureCounterLong(GovernanceConventions.AuditWriteFailures, action);

        sut.Log("agent-1", action, "allowed");

        failures.Should().ContainSingle();
    }

    [Fact]
    public async Task GetRecordsAsync_EmptyStore_ReturnsEmptySuccess()
    {
        using var sut = NewWriter();

        var result = await sut.GetRecordsAsync(new GovernanceAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecordsAsync_ReturnsRecordsWrittenByLog_InChronologicalOrder()
    {
        using var sut = NewWriter();
        sut.Log("agent-1", "run_tests", "allowed");
        sut.Log("agent-2", "iac_plan", "denied");

        var result = await sut.GetRecordsAsync(new GovernanceAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value![0].AgentId.Should().Be("agent-1");
        result.Value[0].Action.Should().Be("run_tests");
        result.Value[0].Decision.Should().Be("allowed");
        result.Value[1].AgentId.Should().Be("agent-2");
    }

    [Fact]
    public async Task GetRecordsAsync_FiltersByAgentId()
    {
        using var sut = NewWriter();
        sut.Log("agent-1", "run_tests", "allowed");
        sut.Log("agent-2", "iac_plan", "denied");

        var result = await sut.GetRecordsAsync(
            new GovernanceAuditQuery { AgentId = "agent-2" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.AgentId == "agent-2");
    }

    [Fact]
    public async Task GetRecordsAsync_FiltersByDateRange_Inclusive()
    {
        using var sut = NewWriter();
        sut.Log("agent-1", "run_tests", "allowed");
        var afterFirst = DateTimeOffset.UtcNow;
        await Task.Delay(10);
        sut.Log("agent-2", "iac_plan", "denied");

        var result = await sut.GetRecordsAsync(
            new GovernanceAuditQuery { Start = afterFirst }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.AgentId == "agent-2");
    }

    [Fact]
    public async Task GetRecordsAsync_CorruptedLine_IsSkippedNotFatal()
    {
        using (var sut = NewWriter())
        {
            sut.Log("agent-1", "run_tests", "allowed");
        }

        // Corrupt the JSON payload in place while keeping the tab-delimited chain framing intact,
        // so the line still parses as a chain record but fails to deserialize as GovernanceAuditRecord.
        var corrupted = File.ReadAllText(AuditTrailFiles.OnlyDayFile(_expectedFile)).Replace("\"agent_id\":\"agent-1\"", "not json{{{");
        File.WriteAllText(AuditTrailFiles.OnlyDayFile(_expectedFile), corrupted);

        using var reopened = NewWriter();
        var result = await reopened.GetRecordsAsync(new GovernanceAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a corrupted line must be skipped, not fail the whole query");
        result.Value.Should().BeEmpty();
    }

    /// <summary>
    /// Captures long-counter measurements for the named instrument on the shared meter, filtered to
    /// the given <see cref="GovernanceConventions.Action"/> tag value.
    /// </summary>
    private static List<long> CaptureCounterLong(string instrumentName, string action)
    {
        var values = new List<long>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == GovernanceConventions.Action && Equals(tag.Value, action))
                {
                    values.Add(value);
                    break;
                }
            }
        });
        listener.Start();
        return values;
    }
}
