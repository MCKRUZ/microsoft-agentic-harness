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
/// domain decision faithfully (identity, verdict, reason, server-stamped timestamp), passes the
/// non-conflict service statuses through as reportable data, and translates the two conflict
/// statuses into a <c>Conflict</c> failure. That translation lives here, in the Application layer,
/// so every consumer inherits it rather than each transport re-deriving it; these tests are
/// therefore the guard that survives a transport dropping its own mapping.
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
    public async Task Handle_AwaitingReconciliation_ReturnsConflictFailure()
    {
        // Reachable in the DEFAULT durability-off config: approver A's decision resolves the
        // escalation, the fail-closed audit write throws, the escalation parks with
        // ResolutionFailed set, and approver B's decision then comes back AwaitingReconciliation.
        // It is translated HERE rather than at a transport so every consumer — including the
        // console approvals example, which never touches HTTP — sees the conflict.
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationDecisionResult.AwaitingReconciliation());

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(Domain.Common.ResultFailureType.Conflict,
            "the verdict is already decided, so this vote will never be counted no matter how often " +
            "the request is retried — a state conflict, not transient unavailability");
        result.Errors.Should().ContainSingle().Which.Should().Contain("not counted",
            "the approver must be told plainly that their vote did not participate");
    }

    [Theory]
    [InlineData(EscalationDecisionStatus.ConflictingDecision)]
    [InlineData(EscalationDecisionStatus.AwaitingReconciliation)]
    public async Task Handle_ConflictStatuses_CarryTheirOwnDistinctDetail(EscalationDecisionStatus status)
    {
        // Both conflicts share a status code but not a cause, and an approver acting on the
        // message needs to know which happened: "your vote was rejected because you already voted
        // the other way" and "the escalation was already decided without your vote" call for
        // different next steps. Collapsing them to one generic 409 body would hide that.
        _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResultForStatus(status));

        var result = await _handler.Handle(NewCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        var expectedFragment = status == EscalationDecisionStatus.ConflictingDecision
            ? "votes cannot be changed"
            : "awaiting reconciliation";
        result.Errors.Should().ContainSingle().Which.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task Handle_EveryDecisionStatus_IsEitherConflictFailureOrPassedThroughAsData()
    {
        // The guard for the whole defect class, and the reason it lives here rather than in the
        // controller: a status added to the enum without a decision about its meaning used to
        // reach the controller's switch and fall through to a 500. This handler is now the single
        // place that classifies conflicts, so an unclassified member must fail HERE — where every
        // consumer inherits the answer — not once per transport.
        foreach (var status in Enum.GetValues<EscalationDecisionStatus>())
        {
            _service.Reset();
            _service.Setup(s => s.SubmitDecisionAsync(It.IsAny<Guid>(), It.IsAny<ApproverDecision>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ResultForStatus(status));

            var result = await _handler.Handle(NewCommand(), CancellationToken.None);

            if (result.IsSuccess)
            {
                result.Value!.Status.Should().Be(status,
                    $"EscalationDecisionStatus.{status} must reach the transport unchanged when it is not a conflict");
            }
            else
            {
                result.FailureType.Should().Be(Domain.Common.ResultFailureType.Conflict,
                    $"EscalationDecisionStatus.{status} is reported as a failure, and Conflict is the only " +
                    "failure type this handler is allowed to produce — anything else maps to a 5xx");
                result.Errors.Should().NotBeEmpty(
                    $"EscalationDecisionStatus.{status} is rejected, so the caller must be told why");
            }
        }
    }

    private static EscalationDecisionResult ResultForStatus(EscalationDecisionStatus status) => status switch
    {
        EscalationDecisionStatus.UnknownEscalation => EscalationDecisionResult.UnknownEscalation(),
        EscalationDecisionStatus.ApproverNotAuthorized => EscalationDecisionResult.ApproverNotAuthorized(),
        EscalationDecisionStatus.DecisionRecorded => EscalationDecisionResult.DecisionRecorded(),
        EscalationDecisionStatus.ConflictingDecision => EscalationDecisionResult.ConflictingDecision(),
        EscalationDecisionStatus.AwaitingReconciliation => EscalationDecisionResult.AwaitingReconciliation(),
        EscalationDecisionStatus.Resolved =>
            EscalationDecisionResult.Resolved(EscalationTestData.NewOutcome(Guid.NewGuid(), approved: true)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status,
            "A new EscalationDecisionStatus has no test factory; add one so the exhaustiveness guard covers it.")
    };

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
