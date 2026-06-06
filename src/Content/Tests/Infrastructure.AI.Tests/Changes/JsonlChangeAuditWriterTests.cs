using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes;

public sealed class JsonlChangeAuditWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonlChangeAuditWriter _sut;
    private readonly string _expectedFile;

    public JsonlChangeAuditWriterTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _sut = new JsonlChangeAuditWriter(monitor, NullLogger<JsonlChangeAuditWriter>.Instance);
        _expectedFile = Path.Combine(dir, "audit", "changes.jsonl");
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Append_WritesOneLinePerDecision()
    {
        var proposal = TestProposals.NewProposal();
        var d1 = NewDecision("self_validation", GateAction.Pass);
        var d2 = NewDecision("approval", GateAction.Fail, "bad day");

        await _sut.AppendAsync(proposal, d1, proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);
        await _sut.AppendAsync(proposal, d2, proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(_expectedFile);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_IncludesAllExpectedFields()
    {
        var proposal = TestProposals.NewProposal();
        var d1 = NewDecision("policy", GateAction.Pass, "ok", evidenceHash: "sha256:abc");

        await _sut.AppendAsync(proposal, d1, proposal.SubmittedBy, OrchestratorMode.Shadow, "corr-1", CancellationToken.None);

        var line = (await File.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().Contain("\"proposal_id\":");
        line.Should().Contain("\"gate_key\":\"policy\"");
        line.Should().Contain("\"decision\":\"Pass\"");
        line.Should().Contain("\"mode\":\"Shadow\"");
        line.Should().Contain("\"correlation_id\":\"corr-1\"");
        line.Should().Contain("\"evidence_hash\":\"sha256:abc\"");
        line.Should().Contain("\"agent\":\"agent-001\"");
        line.Should().Contain("\"tenant\":\"tenant-A\"");
        line.Should().Contain("\"blast_radius\":\"Low\"");
        line.Should().Contain("\"target_kind\":\"GitRepo\"");
    }

    [Fact]
    public async Task Append_ShadowMode_DistinguishableInAuditLine()
    {
        var proposal = TestProposals.NewProposal();
        await _sut.AppendAsync(proposal, NewDecision("x", GateAction.Pass), proposal.SubmittedBy, OrchestratorMode.Shadow, "c", CancellationToken.None);

        var line = (await File.ReadAllLinesAsync(_expectedFile))[0];
        line.Should().Contain("\"mode\":\"Shadow\"");
    }

    private static GateDecision NewDecision(string key, GateAction action, string reason = "", string? evidenceHash = null) =>
        new()
        {
            Timestamp = TestProposals.DefaultTime,
            GateKey = key,
            Action = action,
            Reason = reason,
            EvidenceHash = evidenceHash,
            DurationMs = 12
        };
}
