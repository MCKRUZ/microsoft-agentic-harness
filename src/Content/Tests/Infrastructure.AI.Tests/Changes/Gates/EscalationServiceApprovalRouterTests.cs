using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Changes;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes.Gates;

public sealed class EscalationServiceApprovalRouterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig> _monitor;

    public EscalationServiceApprovalRouterTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _monitor = monitor;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class RecordingEscalationService : IEscalationService
    {
        public EscalationRequest? LastRequest { get; private set; }
        public int QueueCount { get; private set; }

        public Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            QueueCount++;
            return Task.FromResult(request.EscalationId);
        }

        public Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<EscalationOutcome?> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct) => throw new NotImplementedException();
        public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EscalationOutcome?> GetOutcomeAsync(Guid escalationId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct) => throw new NotImplementedException();
        public Task<EscalationOutcome> CancelEscalationAsync(Guid escalationId, string reason, CancellationToken ct) => throw new NotImplementedException();
    }

    private static GateContext Ctx(int attempt = 1) => new()
    {
        Mode = OrchestratorMode.Live,
        AttemptCount = attempt,
        EvaluatedAt = TestProposals.DefaultTime,
        CorrelationId = "corr-1"
    };

    private EscalationServiceApprovalRouter Build(RecordingEscalationService svc) =>
        new(svc, _monitor, TimeProvider.System, NullLogger<EscalationServiceApprovalRouter>.Instance);

    [Fact]
    public async Task RouteAsync_EmptyApproversList_Throws()
    {
        var svc = new RecordingEscalationService();
        // monitor default has DefaultApprovers = []
        var sut = Build(svc);

        var act = async () => await sut.RouteAsync(TestProposals.NewProposal(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DefaultApprovers*empty*");
        svc.QueueCount.Should().Be(0);
    }

    [Fact]
    public async Task RouteAsync_WithApprovers_BuildsAndQueuesRequest()
    {
        var svc = new RecordingEscalationService();
        _monitor.CurrentValue.AI.Changes.DefaultApprovers = ["alice", "bob"];
        var sut = Build(svc);
        var proposal = TestProposals.NewProposal();

        await sut.RouteAsync(proposal, Ctx(), CancellationToken.None);

        svc.QueueCount.Should().Be(1);
        svc.LastRequest!.AgentId.Should().Be(proposal.SubmittedBy.Id);
        svc.LastRequest.Approvers.Should().Equal("alice", "bob");
        svc.LastRequest.Description.Should().Be(proposal.Summary);
        svc.LastRequest.Arguments.Should().ContainKey("proposal_id");
        svc.LastRequest.Arguments["proposal_id"].Should().Be(proposal.Id);
        svc.LastRequest.ToolName.Should().Contain("change_proposal");
    }

    [Theory]
    [InlineData(BlastRadius.Trivial, RiskLevel.Low, EscalationPriority.Informational)]
    [InlineData(BlastRadius.Low, RiskLevel.Low, EscalationPriority.Informational)]
    [InlineData(BlastRadius.Medium, RiskLevel.Medium, EscalationPriority.Blocking)]
    [InlineData(BlastRadius.High, RiskLevel.High, EscalationPriority.Blocking)]
    [InlineData(BlastRadius.Critical, RiskLevel.Critical, EscalationPriority.Critical)]
    public async Task RouteAsync_MapsBlastRadius_ToRiskAndPriority(BlastRadius radius, RiskLevel expectedRisk, EscalationPriority expectedPriority)
    {
        var svc = new RecordingEscalationService();
        _monitor.CurrentValue.AI.Changes.DefaultApprovers = ["alice"];
        var sut = Build(svc);

        await sut.RouteAsync(TestProposals.NewProposal(blastRadius: radius), Ctx(), CancellationToken.None);

        svc.LastRequest!.RiskLevel.Should().Be(expectedRisk);
        svc.LastRequest.Priority.Should().Be(expectedPriority);
    }

    [Fact]
    public async Task RouteAsync_SameProposalSameAttempt_ProducesSameEscalationId()
    {
        var svc1 = new RecordingEscalationService();
        var svc2 = new RecordingEscalationService();
        _monitor.CurrentValue.AI.Changes.DefaultApprovers = ["alice"];
        var sut1 = Build(svc1);
        var sut2 = Build(svc2);
        var proposal = TestProposals.NewProposal();

        await sut1.RouteAsync(proposal, Ctx(attempt: 2), CancellationToken.None);
        await sut2.RouteAsync(proposal, Ctx(attempt: 2), CancellationToken.None);

        svc1.LastRequest!.EscalationId.Should().Be(svc2.LastRequest!.EscalationId);
    }

    [Fact]
    public async Task RouteAsync_DifferentAttempt_ProducesDifferentEscalationId()
    {
        var svc = new RecordingEscalationService();
        _monitor.CurrentValue.AI.Changes.DefaultApprovers = ["alice"];
        var sut = Build(svc);
        var proposal = TestProposals.NewProposal();

        await sut.RouteAsync(proposal, Ctx(attempt: 1), CancellationToken.None);
        var id1 = svc.LastRequest!.EscalationId;
        await sut.RouteAsync(proposal, Ctx(attempt: 2), CancellationToken.None);
        var id2 = svc.LastRequest!.EscalationId;

        id1.Should().NotBe(id2);
    }
}
