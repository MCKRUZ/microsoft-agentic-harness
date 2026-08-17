using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Audit;
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
        return new StaticOptionsMonitor(cfg);
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

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public StaticOptionsMonitor(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
