using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.Core.CQRS.Compliance.GenerateComplianceReport;
using Domain.AI.Audit;
using Domain.AI.Changes;
using Domain.AI.DriftDetection;
using Domain.AI.Egress;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Observability.Models;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Compliance;

/// <summary>
/// Tests for <see cref="GenerateComplianceReportQueryHandler"/>: the report joins every source
/// honestly, a source failure lands in <see cref="Domain.AI.Compliance.ComplianceReport.Warnings"/>
/// rather than reading as "nothing happened", and conversation scoping narrows the
/// session/safety/audit-log sections only.
/// </summary>
public sealed class GenerateComplianceReportQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IObservabilityStore> _store = new();
    private readonly Mock<IGovernanceAuditService> _governance = new();
    private readonly Mock<IChangeAuditWriter> _change = new();
    private readonly Mock<IEgressAuditWriter> _egress = new();
    private readonly Mock<IEscalationAuditStore> _escalation = new();
    private readonly Mock<IDriftAuditStore> _drift = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly Mock<IAuditChainCheckpoints> _checkpoints = new();

    private GenerateComplianceReportQueryHandler CreateSut(IEnumerable<IVerifiableAuditChain>? chains = null) =>
        new(_store.Object, _governance.Object, _change.Object, _egress.Object, _escalation.Object, _drift.Object,
            chains ?? [], _checkpoints.Object, _timeProvider, NullLogger<GenerateComplianceReportQueryHandler>.Instance);

    private static GenerateComplianceReportQuery NewQuery(string? conversationId = null) => new()
    {
        CallerId = "operator-1",
        Start = Now.AddDays(-7),
        End = Now,
        ConversationId = conversationId,
    };

    private void SetUpAllSourcesEmpty()
    {
        _store.Setup(s => s.GetSessionsAsync(
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionRecord>)[]);
        _store.Setup(s => s.GetSafetyEventsAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<SafetyEventRecord>>.Success([]));
        _store.Setup(s => s.GetAuditEntriesAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<AuditEntry>>.Success([]));

        _governance.Setup(g => g.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Success([]));
        _change.Setup(c => c.GetRecordsAsync(It.IsAny<ChangeAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ChangeAuditRecord>>.Success([]));
        _egress.Setup(e => e.GetRecordsAsync(It.IsAny<EgressAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EgressAuditRecord>>.Success([]));
        _escalation.Setup(e => e.QueryAsync(It.IsAny<EscalationAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<EscalationAuditRecord>>.Success([]));
        _drift.Setup(d => d.GetRecordsAsync(It.IsAny<DriftAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftAuditRecord>>.Success([]));
    }

    [Fact]
    public async Task Handle_AllSourcesEmpty_ReturnsCleanReportWithNoWarnings()
    {
        SetUpAllSourcesEmpty();
        var sut = CreateSut();

        var result = await sut.Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().BeEmpty();
        result.Value.GeneratedBy.Should().Be("operator-1");
        result.Value.GeneratedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Handle_SafetyEventsSourceFails_RecordsWarning_DoesNotReadAsClean()
    {
        SetUpAllSourcesEmpty();
        _store.Setup(s => s.GetSafetyEventsAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<SafetyEventRecord>>.Fail("database unavailable"));
        var sut = CreateSut();

        var result = await sut.Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the report itself still generates from the sources that did answer");
        result.Value!.Warnings.Should().ContainSingle(w => w.Contains("safety events") && w.Contains("database unavailable"));
        result.Value.SafetyEvents.Should().BeEmpty();
        result.Value.Safety.TotalEvents.Should().Be(0,
            "an empty count here must be explained by the warning above, not read as a clean period");
    }

    [Fact]
    public async Task Handle_GovernanceSourceFails_RecordsWarning()
    {
        SetUpAllSourcesEmpty();
        _governance.Setup(g => g.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Fail("chain read error"));
        var sut = CreateSut();

        var result = await sut.Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().ContainSingle(w => w.Contains("governance decisions"));
        result.Value.GovernanceDecisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ConversationScoped_FiltersSessionsSafetyAndAuditToThatConversation()
    {
        var matchingSessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();

        SetUpAllSourcesEmpty();
        _store.Setup(s => s.GetSessionsAsync(
                It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionRecord>)
            [
                new SessionRecord { Id = matchingSessionId, ConversationId = "conv-a", AgentName = "A", Status = "completed" },
                new SessionRecord { Id = otherSessionId, ConversationId = "conv-b", AgentName = "A", Status = "completed" },
            ]);
        _store.Setup(s => s.GetSafetyEventsAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<SafetyEventRecord>>.Success(
            [
                new SafetyEventRecord { SessionId = matchingSessionId, Phase = "prompt", Outcome = "pass" },
                new SafetyEventRecord { SessionId = otherSessionId, Phase = "prompt", Outcome = "block" },
            ]));
        _store.Setup(s => s.GetAuditEntriesAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<AuditEntry>>.Success(
            [
                new AuditEntry { Operation = "op-a", Source = "harness", SessionId = matchingSessionId },
                new AuditEntry { Operation = "op-b", Source = "harness", SessionId = otherSessionId },
            ]));
        var sut = CreateSut();

        var result = await sut.Handle(NewQuery(conversationId: "conv-a"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Sessions.TotalSessions.Should().Be(1);
        result.Value.SafetyEvents.Should().ContainSingle(e => e.SessionId == matchingSessionId);
        result.Value.AuditEntries.Should().ContainSingle(e => e.SessionId == matchingSessionId);
    }

    [Fact]
    public async Task Handle_MoreMatchesThanCap_ReturnsMostRecentInChronologicalOrder()
    {
        var baseTime = Now.AddDays(-5);
        var records = Enumerable.Range(0, 5)
            .Select(i => new GovernanceAuditRecord
            {
                Timestamp = baseTime.AddMinutes(i), AgentId = "a", Action = "act", Decision = "allowed",
            })
            .ToList();
        SetUpAllSourcesEmpty();
        _governance.Setup(g => g.GetRecordsAsync(It.IsAny<GovernanceAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<GovernanceAuditRecord>>.Success(records));
        var sut = CreateSut();

        var result = await sut.Handle(NewQuery() with { MaxRecordsPerSource = 2 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.GovernanceDecisions.Should().HaveCount(2);
        result.Value.GovernanceDecisions.Should().ContainInOrder(records[3], records[4]);
    }

    [Fact]
    public async Task Handle_VerifiesEveryRegisteredChain_AndSurfacesABrokenOne()
    {
        SetUpAllSourcesEmpty();
        var intactChain = new Mock<IVerifiableAuditChain>();
        intactChain.SetupGet(c => c.AuditName).Returns("drift");
        intactChain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuditChainVerificationResult.Valid(42));

        var brokenChain = new Mock<IVerifiableAuditChain>();
        brokenChain.SetupGet(c => c.AuditName).Returns("governance");
        brokenChain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuditChainVerificationResult.Broken(10, 11, "record-hash mismatch"));

        var sut = CreateSut([intactChain.Object, brokenChain.Object]);

        var result = await sut.Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChainIntegrity.Should().HaveCount(2);
        result.Value.ChainIntegrity.Should().ContainSingle(c => c.ChainName == "drift" && c.IsValid && c.VerifiedCount == 42);
        result.Value.ChainIntegrity.Should().ContainSingle(
            c => c.ChainName == "governance" && !c.IsValid && c.FailureReason == "record-hash mismatch");
    }

    [Fact]
    public async Task Handle_RecordsItsOwnGenerationAsAnAuditEntry()
    {
        SetUpAllSourcesEmpty();
        var sut = CreateSut();

        await sut.Handle(NewQuery(), CancellationToken.None);

        _store.Verify(s => s.RecordAuditAsync(
            "compliance_report_generated", "harness", null,
            It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    /// <summary>
    /// #416 review: the report used to verify internal consistency only, so it certified as intact a log
    /// the scheduled verifier reported truncated. It now verifies against the verifier's last checkpoint.
    /// </summary>
    [Fact]
    public async Task Handle_VerifiesEachChainAgainstTheVerifiersLastCheckpoint()
    {
        SetUpAllSourcesEmpty();
        var checkpoint = new AuditChainCheckpoint(41, "hash-41");
        var chain = new Mock<IVerifiableAuditChain>();
        chain.SetupGet(c => c.AuditName).Returns("drift");
        chain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuditChainVerificationResult.Valid(42));
        _checkpoints.Setup(c => c.ReadAsync(chain.Object, It.IsAny<CancellationToken>())).ReturnsAsync(checkpoint);

        await CreateSut([chain.Object]).Handle(NewQuery(), CancellationToken.None);

        chain.Verify(c => c.VerifyChainAsync(checkpoint, It.IsAny<CancellationToken>()), Times.Once);
        _checkpoints.Verify(
            c => c.WriteAsync(It.IsAny<IVerifiableAuditChain>(), It.IsAny<AuditChainCheckpoint>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a report reads the checkpoint; only the scheduled verifier advances it");
    }

    /// <summary>
    /// #416: a chain whose lock another writer holds past the timeout is not verified. The report must
    /// neither certify it intact nor fail as a whole; the other chains are still reported.
    /// </summary>
    [Fact]
    public async Task Handle_ABusyChain_IsReportedAsNotVerifiedAndTheRestStillRun()
    {
        SetUpAllSourcesEmpty();
        var busyChain = new Mock<IVerifiableAuditChain>();
        busyChain.SetupGet(c => c.AuditName).Returns("governance");
        busyChain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuditChainBusyException("governance", TimeSpan.FromSeconds(10), new IOException("locked")));
        var intactChain = new Mock<IVerifiableAuditChain>();
        intactChain.SetupGet(c => c.AuditName).Returns("drift");
        intactChain.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuditChainVerificationResult.Valid(42));

        var result = await CreateSut([busyChain.Object, intactChain.Object]).Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChainIntegrity.Should().ContainSingle(
            c => c.ChainName == "governance" && !c.IsValid && c.FailureReason!.StartsWith("Not verified"));
        result.Value.ChainIntegrity.Should().ContainSingle(c => c.ChainName == "drift" && c.IsValid);
    }

    [Fact]
    public async Task Handle_ARefusedAuditFolder_IsReportedAsNotVerifiedWithTheReason()
    {
        // #416: a file planted in the chain's folder stops it being verified; the report says why, not "intact".
        SetUpAllSourcesEmpty();
        var refused = new Mock<IVerifiableAuditChain>();
        refused.SetupGet(c => c.AuditName).Returns("governance");
        refused.Setup(c => c.VerifyChainAsync(It.IsAny<AuditChainCheckpoint?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuditFolderRefusedException("folder holds 2026-10-21.JSONL"));

        var result = await CreateSut([refused.Object]).Handle(NewQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChainIntegrity.Should().ContainSingle(c =>
            c.ChainName == "governance" && !c.IsValid && c.FailureReason == "Not verified: folder holds 2026-10-21.JSONL");
    }
}
