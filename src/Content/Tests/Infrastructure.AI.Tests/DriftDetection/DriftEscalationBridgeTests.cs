using Application.Core.CQRS.Learnings;
using Domain.AI.DriftDetection;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Learnings;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Application.AI.Common.Interfaces.DriftDetection;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

/// <summary>
/// Tests for <see cref="DriftEscalationBridge"/> -- verifies escalation-to-drift
/// resolution bridging and learning creation from approver corrections.
/// </summary>
public sealed class DriftEscalationBridgeTests
{
    private readonly Mock<IDriftNotifier> _driftNotifierMock = new();
    private readonly Mock<ISender> _senderMock = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly DriftEscalationBridge _sut;

    public DriftEscalationBridgeTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

        _driftNotifierMock
            .Setup(n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _senderMock
            .Setup(s => s.Send(It.IsAny<RememberCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LearningEntry>.Success(CreateLearningEntry()));

        _sut = new DriftEscalationBridge(
            _driftNotifierMock.Object,
            _senderMock.Object,
            _timeProvider,
            NullLogger<DriftEscalationBridge>.Instance);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_DriftOriginated_NotifiesDriftResolved()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: "Accepted");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.Is<DriftEvent>(e =>
                e.Resolution != null &&
                e.Resolution.ResolvedBy == DriftResolutionType.EscalationResolved &&
                e.Resolution.ResolutionId == outcome.EscalationId.ToString() &&
                e.Resolution.ResolvedAt == outcome.ResolvedAt),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_DriftOriginated_CreatesLearning()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: false, reason: "Output was incorrect");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _senderMock.Verify(
            s => s.Send(It.Is<RememberCommand>(cmd =>
                cmd.Source.SourceType == LearningSourceType.EscalationResolution &&
                cmd.Source.SourceId == outcome.EscalationId.ToString() &&
                cmd.Content.Contains("Output was incorrect") &&
                cmd.Category == LearningCategory.FactualCorrection &&
                cmd.Provenance.OriginPipeline == "DriftEscalationBridge" &&
                cmd.Provenance.Confidence == 0.8),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_NonDrift_IgnoresResolution()
    {
        var request = CreateTestRequest("code_execution");
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: "OK");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _senderMock.Verify(
            s => s.Send(It.IsAny<RememberCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_UnknownId_IgnoresResolution()
    {
        var outcome = CreateTestOutcome(Guid.NewGuid(), approved: true, reason: "OK");

        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_FiltersByToolName()
    {
        var driftRequest = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var otherRequest = CreateTestRequest("code_execution");
        var driftOutcome = CreateTestOutcome(driftRequest.EscalationId, approved: true, reason: "Fixed");
        var otherOutcome = CreateTestOutcome(otherRequest.EscalationId, approved: true, reason: "OK");

        await _sut.NotifyEscalationRequestedAsync(driftRequest, CancellationToken.None);
        await _sut.NotifyEscalationRequestedAsync(otherRequest, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(otherOutcome, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(driftOutcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_NoReasons_SkipsLearningCreation()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: null);

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _senderMock.Verify(
            s => s.Send(It.IsAny<RememberCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_Approved_UsesInstructionUpdateCategory()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: "New behavior accepted");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _senderMock.Verify(
            s => s.Send(It.Is<RememberCommand>(cmd =>
                cmd.Category == LearningCategory.InstructionUpdate),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_Denied_UsesFactualCorrectionCategory()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: false, reason: "Regression confirmed");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _senderMock.Verify(
            s => s.Send(It.Is<RememberCommand>(cmd =>
                cmd.Category == LearningCategory.FactualCorrection),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NotifyEscalationExpiringAsync_DoesNotNotifyDrift()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);

        await _sut.NotifyEscalationExpiringAsync(request, TimeSpan.FromSeconds(30), CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyEscalationExpiringAsync_RemovesTrackedEntry()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: "OK");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationExpiringAsync(request, TimeSpan.FromSeconds(30), CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _driftNotifierMock.Verify(
            n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_Exception_DoesNotThrow()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: true, reason: "OK");

        _driftNotifierMock
            .Setup(n => n.NotifyDriftResolvedAsync(It.IsAny<DriftEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Notifier failed"));

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);

        var act = () => _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void DriftDetectionToolName_MatchesConvention()
    {
        DriftEscalationBridge.DriftDetectionToolName.Should().Be("drift_detection");
    }

    [Fact]
    public async Task NotifyEscalationResolvedAsync_ScopeDerivedFromRequest()
    {
        var request = CreateTestRequest(DriftEscalationBridge.DriftDetectionToolName);
        var outcome = CreateTestOutcome(request.EscalationId, approved: false, reason: "Wrong output");

        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);

        _senderMock.Verify(
            s => s.Send(It.Is<RememberCommand>(cmd =>
                cmd.Scope.AgentId == request.AgentId),
            CancellationToken.None),
            Times.Once);
    }

    private static EscalationRequest CreateTestRequest(string toolName) => new()
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = toolName,
        Arguments = new Dictionary<string, string>
        {
            ["score_id"] = Guid.NewGuid().ToString(),
            ["severity"] = "Escalate",
        }.AsReadOnly(),
        Description = "Drift detected in Agent:test-agent — overall 3.50σ",
        RiskLevel = RiskLevel.High,
        Priority = EscalationPriority.Blocking,
        Approvers = ["admin@company.com"],
        RequestedAt = DateTimeOffset.UtcNow,
    };

    private static EscalationOutcome CreateTestOutcome(Guid escalationId, bool approved, string? reason) => new()
    {
        EscalationId = escalationId,
        IsApproved = approved,
        Decisions =
        [
            new ApproverDecision
            {
                ApproverName = "admin@company.com",
                Approved = approved,
                Reason = reason,
                RespondedAt = DateTimeOffset.UtcNow,
            },
        ],
        ResolutionType = approved ? EscalationResolutionType.Approved : EscalationResolutionType.Denied,
        ResolvedAt = DateTimeOffset.UtcNow,
    };

    private static LearningEntry CreateLearningEntry() => new()
    {
        LearningId = Guid.NewGuid(),
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Permanent,
        Scope = new LearningScope { AgentId = "test-agent" },
        Content = "Test learning",
        Source = new LearningSource
        {
            SourceType = LearningSourceType.EscalationResolution,
            SourceId = "test",
            SourceDescription = "Test",
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "DriftEscalationBridge",
            OriginTask = "escalation_resolution",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.8,
        },
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
