using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Audit;
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

    private JsonlGovernanceAuditWriter NewWriter(string? storagePathOverride = null) =>
        new(Monitor(storagePathOverride ?? _tempDir), NullLogger<JsonlGovernanceAuditWriter>.Instance);

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

        File.ReadAllLines(_expectedFile).Should().HaveCount(2);
    }

    [Fact]
    public void Log_IncludesAllExpectedFields()
    {
        using var sut = NewWriter();

        sut.Log("agent-1", "run_tests", "allowed");

        var line = File.ReadAllLines(_expectedFile)[0];
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

        var tampered = File.ReadAllText(_expectedFile).Replace("\"allowed\"", "\"denied\"");
        File.WriteAllText(_expectedFile, tampered);

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
        var corrupted = File.ReadAllText(_expectedFile).Replace("\"agent_id\":\"agent-1\"", "not json{{{");
        File.WriteAllText(_expectedFile, corrupted);

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
