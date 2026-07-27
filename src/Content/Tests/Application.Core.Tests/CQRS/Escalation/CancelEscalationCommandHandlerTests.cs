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
/// Tests for <see cref="CancelEscalationCommandHandler"/> — the handler translates the service's
/// exception-based contract into mappable failures: unknown → NotFound (404), already resolved →
/// Conflict (409), and a lost resolution race → Conflict rather than a 500.
/// </summary>
public sealed class CancelEscalationCommandHandlerTests
{
    private readonly Mock<IEscalationService> _service = new();
    private readonly CancelEscalationCommandHandler _handler;

    public CancelEscalationCommandHandlerTests()
    {
        _handler = new CancelEscalationCommandHandler(
            _service.Object, NullLogger<CancelEscalationCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_PendingEscalation_CancelsAndReturnsDenialOutcome()
    {
        var request = EscalationTestData.NewRequest();
        var id = request.EscalationId;
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _service.Setup(s => s.CancelEscalationAsync(id, "superseded", "admin@contoso.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id, approved: false));

        var result = await _handler.Handle(new CancelEscalationCommand
        {
            EscalationId = id,
            Reason = "superseded",
            CancelledBy = "admin@contoso.com"
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsApproved.Should().BeFalse("cancellation resolves the escalation as denied");
        _service.Verify(
            s => s.CancelEscalationAsync(id, "superseded", "admin@contoso.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_UnknownEscalation_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationOutcome?)null);

        var result = await _handler.Handle(NewCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _service.Verify(
            s => s.CancelEscalationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyResolvedEscalation_ReturnsConflict()
    {
        var id = Guid.NewGuid();
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((EscalationRequest?)null);
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id));

        var result = await _handler.Handle(NewCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict);
        _service.Verify(
            s => s.CancelEscalationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_DecisionWinsRaceDuringCancel_ReturnsConflictNot500()
    {
        // The service throws InvalidOperationException when a decision or timeout wins the race
        // between the pending pre-check and the cancel call; that is an expected concurrency
        // outcome and must surface as 409, never as an unclassified 500.
        var request = EscalationTestData.NewRequest();
        var id = request.EscalationId;
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _service.Setup(s => s.CancelEscalationAsync(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Escalation {id} is already resolved"));
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EscalationTestData.NewOutcome(id, approved: true));

        var result = await _handler.Handle(NewCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse("an approval outcome is not this caller's cancellation");
        result.FailureType.Should().Be(ResultFailureType.Conflict);
    }

    [Fact]
    public async Task Handle_OwnCancellationWinsRace_ReturnsSuccessNotConflict()
    {
        // A duplicated/retried cancel whose first attempt already won must not misreport 409:
        // the recorded outcome IS this caller's own denial, so the desired end state holds.
        var request = EscalationTestData.NewRequest();
        var id = request.EscalationId;
        var ownCancellation = EscalationTestData.NewOutcome(id, approved: false) with
        {
            CancelledBy = "Admin@Contoso.COM" // casing differs from the command's actor
        };
        _service.Setup(s => s.GetPendingEscalationAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);
        _service.Setup(s => s.CancelEscalationAsync(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Escalation {id} is already resolved"));
        _service.Setup(s => s.GetOutcomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ownCancellation);

        var result = await _handler.Handle(NewCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the caller's own earlier cancellation already produced the desired end state");
        result.Value!.IsApproved.Should().BeFalse();
    }

    private static CancelEscalationCommand NewCommand(Guid id) => new()
    {
        EscalationId = id,
        Reason = "superseded",
        CancelledBy = "admin@contoso.com"
    };
}
