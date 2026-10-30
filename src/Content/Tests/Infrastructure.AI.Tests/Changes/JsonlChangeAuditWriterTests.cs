using Infrastructure.AI.Tests.Audit;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;
using Infrastructure.AI.Telemetry.Redaction;

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
        _sut = new JsonlChangeAuditWriter(monitor, TimeProvider.System, NullLogger<JsonlChangeAuditWriter>.Instance, AuditPseudonyms.Create(), new DefaultContentRedactionFilter());
        _expectedFile = Path.Combine(dir, "audit", "changes.jsonl");
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Append_TheChainCannotBeWritten_StillRefusesTheStepButHoldsTheDecisionMarked()
    {
        var proposal = TestProposals.NewProposal();
        var dayFolder = Path.Combine(_tempDir, "audit", "changes");
        Directory.CreateDirectory(Path.GetDirectoryName(dayFolder)!);
        File.WriteAllText(dayFolder, "not a folder");

        var append = () => _sut.AppendAsync(
            proposal, NewDecision("approval", GateAction.Pass), proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);

        await append.Should().ThrowAsync<IOException>("a gate decision that cannot be recorded must not take effect");
        File.ReadAllText(Directory.GetFiles(_sut.PendingLocation, "*.pending").Single()).Should().Contain(
            "\"not_carried_out\":true", "the decision is kept, marked as one whose transition never happened");

        File.Delete(dayFolder);
        (await _sut.ReplayPendingAsync(CancellationToken.None)).Written.Should().Be(1);
        var replayed = (await _sut.GetRecordsAsync(new ChangeAuditQuery(), CancellationToken.None)).Value!.Single();
        replayed.NotCarriedOut.Should().BeTrue();
        (await _sut.GetRecordsAsync(new ChangeAuditQuery { Decision = GateAction.Pass }, CancellationToken.None)).Value!
            .Should().BeEmpty("a decision that never took effect answers no decision filter");
    }

    [Fact]
    public async Task Append_WritesOneLinePerDecision()
    {
        var proposal = TestProposals.NewProposal();
        var d1 = NewDecision("self_validation", GateAction.Pass);
        var d2 = NewDecision("approval", GateAction.Fail, "bad day");

        await _sut.AppendAsync(proposal, d1, proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);
        await _sut.AppendAsync(proposal, d2, proposal.SubmittedBy, OrchestratorMode.Live, "corr-1", CancellationToken.None);

        var lines = await AuditTrailFiles.ReadAllLinesAsync(_expectedFile);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_IncludesAllExpectedFields()
    {
        var proposal = TestProposals.NewProposal();
        var d1 = NewDecision("policy", GateAction.Pass, "ok", evidenceHash: "sha256:abc");

        await _sut.AppendAsync(proposal, d1, proposal.SubmittedBy, OrchestratorMode.Shadow, "corr-1", CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
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
    public async Task Append_RecordsTheReviewerAsAReferenceAndTheReasonRedacted()
    {
        // The trail is kept for good (#903): the human reviewer as the reference the key holder can recompute, the
        // orchestrator's own auto-approval readable as it is, and the reason with identifiers taken out.
        var proposal = TestProposals.NewProposal();
        var human = NewDecision("approval", GateAction.Pass, "ok, dana@contoso.com signed off") with { ReviewerId = "alice@contoso.com" };
        var automatic = NewDecision("approval", GateAction.Pass) with { ReviewerId = "auto-approver" };

        await _sut.AppendAsync(proposal, human, proposal.SubmittedBy, OrchestratorMode.Live, "c1", CancellationToken.None);
        await _sut.AppendAsync(proposal, automatic, proposal.SubmittedBy, OrchestratorMode.Live, "c2", CancellationToken.None);

        var lines = await AuditTrailFiles.ReadAllLinesAsync(_expectedFile);
        lines[0].Should().NotContain("@contoso.com")
            .And.Contain($"\"reviewer_id\":\"{AuditPseudonyms.Create().Person("alice@contoso.com")}\"")
            .And.Contain("\"reason\":\"ok, [REDACTED");
        lines[1].Should().Contain("\"reviewer_id\":\"auto-approver\"", "a system id names no one");
    }

    [Fact]
    public async Task Append_ShadowMode_DistinguishableInAuditLine()
    {
        var proposal = TestProposals.NewProposal();
        await _sut.AppendAsync(proposal, NewDecision("x", GateAction.Pass), proposal.SubmittedBy, OrchestratorMode.Shadow, "c", CancellationToken.None);

        var line = (await AuditTrailFiles.ReadAllLinesAsync(_expectedFile))[0];
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
        await File.AppendAllTextAsync(AuditTrailFiles.OnlyDayFile(_expectedFile), "not-valid-json\tabc\tdef\t999\n");

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
