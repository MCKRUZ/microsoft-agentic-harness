using Application.AI.Common.Interfaces.Escalation;
using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="SubmitEscalationDecisionCommandHandler"/> — the handler builds the
/// domain decision faithfully (identity, verdict, reason, server-stamped timestamp) and passes
/// every one of the four discriminated service statuses through as reportable data, never as a
/// failure.
/// </summary>
public sealed class SubmitEscalationDecisionCommandHandlerTests
{
    private readonly Mock<IEscalationService> _service = new();
    private readonly SubmitEscalationDecisionCommandHandler _handler;

    public SubmitEscalationDecisionCommandHandlerTests()
    {
        _handler = new SubmitEscalationDecisionCommandHandler(
            _service.Object, NullLogger<SubmitEscalationDecisionCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_BuildsDecisionFromCommandAndStampsRespondedAt()
    {
        var id = Guid.NewGuid();
        ApproverDecision? captured = null;
        _service.Setup(s => s.SubmitDecisionAsync(id, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ApproverDecision, CancellationToken>((_, d, _) => captured = d)
            .ReturnsAsync(EscalationDecisionResult.DecisionRecorded());

        var before = DateTimeOffset.UtcNow;
        await _handler.Handle(new SubmitEscalationDecisionCommand
        {
            EscalationId = id,
            ApproverName = "alice@contoso.com",
            Approve = false,
            Reason = "touches production config"
        }, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        captured.Should().NotBeNull();
        captured!.ApproverName.Should().Be("alice@contoso.com");
        captured.Approved.Should().BeFalse();
        captured.Reason.Should().Be("touches production config");
        captured.RespondedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "the response time must be stamped server-side, never caller-supplied");
    }

    [Fact]
    public async Task Handle_UnknownEscalation_ReturnsSuccessCarryingUnknownStatus()
    {
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.UnknownEscalation());

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the discriminated status is data for the transport to map, not a handler failure");
        result.Value!.Status.Should().Be(EscalationDecisionStatus.UnknownEscalation);
        result.Value.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ApproverNotAuthorized_ReturnsSuccessCarryingNotAuthorizedStatus()
    {
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.ApproverNotAuthorized());

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(EscalationDecisionStatus.ApproverNotAuthorized);
        result.Value.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Handle_DecisionRecorded_ReturnsSuccessCarryingRecordedStatus()
    {
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.DecisionRecorded());

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(EscalationDecisionStatus.DecisionRecorded);
        result.Value.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ConflictingDecision_ReturnsConflictFailure()
    {
        // A changed vote (same approver, opposite verdict) is the one status that IS a request
        // failure: it must surface as ResultFailureType.Conflict so the shared mapper emits 409,
        // never as a 202 that pretends the change was recorded.
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.ConflictingDecision());

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Domain.Common.ResultFailureType.Conflict);
    }

    [Fact]
    public async Task Handle_Resolved_ReturnsSuccessCarryingProjectedOutcome()
    {
        var id = Guid.NewGuid();
        var outcome = EscalationTestData.NewOutcome(id, approved: true);
        _service.Setup(s => s.SubmitDecisionAsync(id, It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.Resolved(outcome));

        var result = await _handler.Handle(NewCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(EscalationDecisionStatus.Resolved);
        result.Value.Outcome.Should().NotBeNull();
        result.Value.Outcome!.EscalationId.Should().Be(id);
        result.Value.Outcome.IsApproved.Should().BeTrue();
        result.Value.Outcome.Decisions.Should().HaveCount(1);
    }

    private static SubmitEscalationDecisionCommand NewCommand(Guid? id = null) => new()
    {
        EscalationId = id ?? Guid.NewGuid(),
        ApproverName = "alice@contoso.com",
        Approve = true,
        Reason = null
    };
}
