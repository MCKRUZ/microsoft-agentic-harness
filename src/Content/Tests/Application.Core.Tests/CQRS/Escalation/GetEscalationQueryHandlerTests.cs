using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="GetEscalationQueryHandler"/> — pending reads are roster-private (a
/// non-roster caller gets the same NotFound as an unknown id), roster matching is
/// case-insensitive, and resolved reads surface the outcome for polling after a 202.
/// </summary>
public sealed class GetEscalationQueryHandlerTests
{
    private readonly Mock<IEscalationService> _service = new();
    private readonly Mock<IEscalationAuditStore> _auditStore = new();
    private readonly GetEscalationQueryHandler _handler;

    public GetEscalationQueryHandlerTests()
    {
        _auditStore
            .Setup(s => s.GetLatestExecutionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationExecutionRecord?)null);

        _handler = new GetEscalationQueryHandler(
            _service.Object, _auditStore.Object, NullLogger<GetEscalationQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_PendingAndCallerOnRoster_ReturnsPendingDetail()
    {
        var request = EscalationTestData.NewRequest(approvers: ["alice@contoso.com"]);
        _service.Setup(s => s.GetPendingEscalationAsync(request.EscalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = request.EscalationId,
            ApproverName = "alice@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(EscalationReadStatus.Pending);
        result.Value.Pending!.EscalationId.Should().Be(request.EscalationId);
        result.Value.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PendingRosterMatch_IsCaseInsensitive()
    {
        // ApproverNames.Comparer is the single roster-comparison authority; a caller whose
        // token casing differs from the roster entry must still see the escalation.
        var request = EscalationTestData.NewRequest(approvers: ["Alice@Contoso.com"]);
        _service.Setup(s => s.GetPendingEscalationAsync(request.EscalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = request.EscalationId,
            ApproverName = "alice@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(EscalationReadStatus.Pending);
    }

    [Fact]
    public async Task Handle_PendingButCallerNotOnRoster_ReturnsNotFound()
    {
        // Roster privacy: existence of a pending escalation must not be observable outside its
        // roster, so the failure is NotFound — indistinguishable from an unknown id.
        var request = EscalationTestData.NewRequest(approvers: ["alice@contoso.com"]);
        _service.Setup(s => s.GetPendingEscalationAsync(request.EscalationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = request.EscalationId,
            ApproverName = "mallory@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_ResolvedAndCallerOnOutcomeRoster_ReturnsOutcomeDetail()
    {
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id, approved: true, "Alice@Contoso.com"));

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = id,
            ApproverName = "alice@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the outcome roster match is case-insensitive like every roster check");
        result.Value!.Status.Should().Be(EscalationReadStatus.Resolved);
        result.Value.Outcome!.IsApproved.Should().BeTrue();
        result.Value.Pending.Should().BeNull();
        result.Value.Outcome.Execution.Should().BeNull(
            "the audit store mock returns no execution record by default — control for the " +
            "populated-execution test below");
    }

    [Fact]
    public async Task Handle_ResolvedWithExecutionOutcome_PopulatesExecutionSummary()
    {
        // #396: GET /api/escalations/{id} previously never surfaced the execution outcome that
        // #325 (PR #366) started reporting — this is the fix.
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id, approved: true, "alice@contoso.com"));
        _auditStore
            .Setup(s => s.GetLatestExecutionAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationExecutionRecord.Failed(
                id, "downstream API returned 500", FailureTextSubstitution.None, DateTimeOffset.UtcNow, "agent-turn"));

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = id,
            ApproverName = "alice@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome!.Execution.Should().NotBeNull();
        result.Value.Outcome.Execution!.Status.Should().Be(EscalationExecutionStatus.Failed);
        result.Value.Outcome.Execution.FailureReason.Should().Be("downstream API returned 500");
    }

    [Fact]
    public async Task Handle_ResolvedButCallerNotOnOutcomeRoster_ReturnsNotFound()
    {
        // Roster privacy survives resolution: a verdict is only readable by the identities that
        // were entitled to produce it, and outsiders get the unknown-id NotFound.
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id, approved: true, "alice@contoso.com"));

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = id,
            ApproverName = "mallory@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_UnknownId_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationOutcome?)null);

        var result = await _handler.Handle(new GetEscalationQuery
        {
            EscalationId = id,
            ApproverName = "alice@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }
}
