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

    [Fact]
    public async Task GetRecordsAsync_RoundTripsAWrittenRecord()
    {
        var proposal = TestProposals.NewProposal();
        await _sut.AppendAsync(proposal, NewDecision("policy", GateAction.Pass, "ok"), proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new ChangeAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        var record = result.Value![0];
        record.ProposalId.Should().Be(proposal.Id);
        record.GateKey.Should().Be("policy");
        record.Decision.Should().Be(GateAction.Pass);
        record.Mode.Should().Be("Live");
        record.CorrelationId.Should().Be("corr-1");
        record.AgentIdentity.Agent.Should().Be("agent-001");
    }

    [Fact]
    public async Task GetRecordsAsync_FiltersByDateRange()
    {
        var proposal = TestProposals.NewProposal();
        var early = NewDecision("gate-early", GateAction.Pass) with { Timestamp = TestProposals.DefaultTime };
        var late = NewDecision("gate-late", GateAction.Pass) with { Timestamp = TestProposals.DefaultTime.AddDays(2) };
        await _sut.AppendAsync(proposal, early, proposal.SubmittedBy, OrchestratorMode.Live, "c1", CancellationToken.None);
        await _sut.AppendAsync(proposal, late, proposal.SubmittedBy, OrchestratorMode.Live, "c2", CancellationToken.None);

        var result = await _sut.GetRecordsAsync(new ChangeAuditQuery
        {
            Start = TestProposals.DefaultTime.AddDays(1),
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.GateKey == "gate-late");
    }

    [Fact]
    public async Task GetRecordsAsync_FiltersByProposalIdGateKeyAndDecision()
    {
        var proposalA = TestProposals.NewProposal();
        // BlastRadius is not part of ChangeProposalIdDeriver's canonicalization (target, diff,
        // submittedBy, submittedAt-bucket only), so varying it alone would not change the id —
        // override the id explicitly to get two genuinely distinguishable proposals.
        var proposalB = TestProposals.NewProposal(blastRadius: BlastRadius.Medium) with { Id = "proposal-b" };
        proposalA.Id.Should().NotBe(proposalB.Id, "the two proposals must be distinguishable by id for this test to be meaningful");
        await _sut.AppendAsync(proposalA, NewDecision("gate-1", GateAction.Pass), proposalA.SubmittedBy, OrchestratorMode.Live, "c1", CancellationToken.None);
        await _sut.AppendAsync(proposalA, NewDecision("gate-2", GateAction.Fail), proposalA.SubmittedBy, OrchestratorMode.Live, "c2", CancellationToken.None);
        await _sut.AppendAsync(proposalB, NewDecision("gate-1", GateAction.Pass), proposalB.SubmittedBy, OrchestratorMode.Live, "c3", CancellationToken.None);

        var byProposal = await _sut.GetRecordsAsync(new ChangeAuditQuery { ProposalId = proposalA.Id }, CancellationToken.None);
        byProposal.Value.Should().HaveCount(2);

        var byGate = await _sut.GetRecordsAsync(new ChangeAuditQuery { ProposalId = proposalA.Id, GateKey = "gate-2" }, CancellationToken.None);
        byGate.Value.Should().ContainSingle(r => r.Decision == GateAction.Fail);

        var byDecision = await _sut.GetRecordsAsync(new ChangeAuditQuery { Decision = GateAction.Fail }, CancellationToken.None);
        byDecision.Value.Should().ContainSingle(r => r.GateKey == "gate-2");
    }

    [Fact]
    public async Task GetRecordsAsync_MissingFile_ReturnsEmpty()
    {
        var result = await _sut.GetRecordsAsync(new ChangeAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecordsAsync_CorruptedLine_SkipsItAndReturnsTheRest()
    {
        var proposal = TestProposals.NewProposal();
        await _sut.AppendAsync(proposal, NewDecision("good", GateAction.Pass), proposal.SubmittedBy, OrchestratorMode.Live, "c1", CancellationToken.None);
        await File.AppendAllTextAsync(_expectedFile, "not-valid-json\tabc\tdef\t999\n");

        var result = await _sut.GetRecordsAsync(new ChangeAuditQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.GateKey == "good");
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
