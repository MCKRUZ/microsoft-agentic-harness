using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Reads one escalation as a discriminated <see cref="EscalationDetail"/>:
/// pending summary (roster-gated), resolved outcome, or <c>NotFound</c>.
/// </summary>
/// <remarks>
/// Both paths enforce roster privacy: a caller not on the roster gets the same <c>NotFound</c>
/// as an unknown id, using <see cref="ApproverNames.Comparer"/> so the check matches the decide
/// path exactly. Resolved outcomes carry the roster forward from the originating request
/// (<see cref="EscalationOutcome.Approvers"/>), so a verdict is only readable by the identities
/// that were entitled to produce it; an outcome without a roster denies (fail-closed).
/// </remarks>
public sealed class GetEscalationQueryHandler
    : IRequestHandler<GetEscalationQuery, Result<EscalationDetail>>
{
    private const string NotFoundMessage = "No escalation with the given id is visible to the caller.";

    private readonly IEscalationService _escalations;
    private readonly ILogger<GetEscalationQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GetEscalationQueryHandler"/> class.</summary>
    /// <param name="escalations">The escalation lifecycle service.</param>
    /// <param name="logger">Logger for recording read outcomes (never escalation content).</param>
    public GetEscalationQueryHandler(
        IEscalationService escalations,
        ILogger<GetEscalationQueryHandler> logger)
    {
        _escalations = escalations;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EscalationDetail>> Handle(
        GetEscalationQuery request, CancellationToken cancellationToken)
    {
        var pending = await _escalations.GetPendingEscalationAsync(
            request.EscalationId, cancellationToken);

        if (pending is not null)
        {
            if (!pending.Approvers.Contains(request.ApproverName, ApproverNames.Comparer))
            {
                // Roster privacy: identical response to an unknown id, so a non-roster caller
                // cannot probe for the existence of pending escalations.
                _logger.LogWarning(
                    "Non-roster read of pending escalation {EscalationId} by {ApproverName} returned NotFound",
                    request.EscalationId, request.ApproverName);
                return Result<EscalationDetail>.NotFound(NotFoundMessage);
            }

            return Result<EscalationDetail>.Success(
                EscalationDetail.ForPending(EscalationSummary.FromRequest(pending)));
        }

        var outcome = await _escalations.GetOutcomeAsync(request.EscalationId, cancellationToken);
        if (outcome is not null)
        {
            // Same roster privacy as the pending path: the outcome carries the originating
            // roster, and a caller outside it gets the indistinguishable NotFound. An empty
            // roster (no roster known) denies everyone — fail-closed.
            if (!outcome.Approvers.Contains(request.ApproverName, ApproverNames.Comparer))
            {
                _logger.LogWarning(
                    "Non-roster read of resolved escalation {EscalationId} by {ApproverName} returned NotFound",
                    request.EscalationId, request.ApproverName);
                return Result<EscalationDetail>.NotFound(NotFoundMessage);
            }

            return Result<EscalationDetail>.Success(
                EscalationDetail.ForResolved(EscalationOutcomeSummary.FromOutcome(outcome)));
        }

        return Result<EscalationDetail>.NotFound(NotFoundMessage);
    }
}
