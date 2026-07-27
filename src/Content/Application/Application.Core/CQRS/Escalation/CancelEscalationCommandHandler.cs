using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Cancels a pending escalation via <see cref="IEscalationService.CancelEscalationAsync"/>,
/// translating the service's exception-based contract into the <see cref="Result"/> failures the
/// HTTP surface maps: unknown id → <c>NotFound</c> (404), already resolved → <c>Conflict</c>
/// (409).
/// </summary>
/// <remarks>
/// The service throws the same <see cref="InvalidOperationException"/> for "unknown" and
/// "already resolved", so this handler disambiguates <em>before</em> cancelling: a pending
/// lookup miss followed by a resolved-outcome hit is a conflict; a miss on both is not-found.
/// If the escalation resolves between the pending check and the cancel call, the caught
/// exception is reported as a conflict — the only state that race can reach.
/// </remarks>
public sealed class CancelEscalationCommandHandler
    : IRequestHandler<CancelEscalationCommand, Result<EscalationOutcomeSummary>>
{
    private readonly IEscalationService _escalations;
    private readonly ILogger<CancelEscalationCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="CancelEscalationCommandHandler"/> class.</summary>
    /// <param name="escalations">The escalation lifecycle service.</param>
    /// <param name="logger">Logger recording who cancelled what, and why.</param>
    public CancelEscalationCommandHandler(
        IEscalationService escalations,
        ILogger<CancelEscalationCommandHandler> logger)
    {
        _escalations = escalations;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EscalationOutcomeSummary>> Handle(
        CancelEscalationCommand request, CancellationToken cancellationToken)
    {
        var pending = await _escalations.GetPendingEscalationAsync(
            request.EscalationId, cancellationToken);

        if (pending is null)
        {
            var resolved = await _escalations.GetOutcomeAsync(request.EscalationId, cancellationToken);
            return resolved is not null
                ? Result<EscalationOutcomeSummary>.Conflict(
                    "The escalation is already resolved and can no longer be cancelled.")
                : Result<EscalationOutcomeSummary>.NotFound(
                    "No pending escalation with the given id.");
        }

        try
        {
            var outcome = await _escalations.CancelEscalationAsync(
                request.EscalationId, request.Reason, request.CancelledBy, cancellationToken);

            _logger.LogInformation(
                "Escalation {EscalationId} cancelled by {CancelledBy}: {Reason}",
                request.EscalationId, request.CancelledBy, request.Reason);

            return Result<EscalationOutcomeSummary>.Success(
                EscalationOutcomeSummary.FromOutcome(outcome));
        }
        catch (InvalidOperationException ex)
        {
            return await ReconcileCancelRaceAsync(request, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Disambiguates the cancel race honestly, keyed on what the outcome store actually
    /// retained. A retained outcome proves a durably audited resolution exists: if it is this
    /// very caller's own cancellation (a retried request whose first attempt won), report
    /// success — the desired end state holds; otherwise a decision or timeout genuinely won the
    /// race — 409. NO retained outcome means the resolution's fail-closed audit write did not
    /// complete (the exception may even be the audit store's own) — the escalation was force-
    /// resolved with no durable record, which is a real failure, never a benign 409.
    /// </summary>
    private async Task<Result<EscalationOutcomeSummary>> ReconcileCancelRaceAsync(
        CancelEscalationCommand request, InvalidOperationException ex, CancellationToken cancellationToken)
    {
        var outcome = await _escalations.GetOutcomeAsync(request.EscalationId, cancellationToken);

        if (outcome is null)
        {
            _logger.LogError(ex,
                "Cancel of escalation {EscalationId} by {CancelledBy} failed with no durably audited outcome retained; reporting failure, not conflict",
                request.EscalationId, request.CancelledBy);
            return Result<EscalationOutcomeSummary>.Fail(
                "The cancellation could not be completed. See server logs for details.");
        }

        if (outcome.CancelledBy is not null
            && ApproverNames.Comparer.Equals(outcome.CancelledBy, request.CancelledBy))
        {
            _logger.LogInformation(
                "Cancel of escalation {EscalationId} by {CancelledBy} raced its own earlier cancellation; reporting success",
                request.EscalationId, request.CancelledBy);
            return Result<EscalationOutcomeSummary>.Success(
                EscalationOutcomeSummary.FromOutcome(outcome));
        }

        _logger.LogWarning(ex,
            "Cancel of escalation {EscalationId} by {CancelledBy} lost a resolution race",
            request.EscalationId, request.CancelledBy);
        return Result<EscalationOutcomeSummary>.Conflict(
            "The escalation resolved while the cancellation was being processed.");
    }
}
