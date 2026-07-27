using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Builds the domain <see cref="ApproverDecision"/> (stamping <c>RespondedAt</c> server-side)
/// and submits it via <see cref="IEscalationService.SubmitDecisionAsync"/>. The service's
/// discriminated statuses — unknown, not-authorized, recorded, resolved — are returned as data,
/// not failure: each is an expected, reportable outcome that the controller maps to its
/// documented HTTP status. The one exception is
/// <see cref="EscalationDecisionStatus.ConflictingDecision"/> (same approver, opposite verdict),
/// which is a genuine request conflict and is translated to a
/// <see cref="Result{T}.Conflict"/> failure so the shared failure mapper produces the 409.
/// </summary>
public sealed class SubmitEscalationDecisionCommandHandler
    : IRequestHandler<SubmitEscalationDecisionCommand, Result<SubmitEscalationDecisionResult>>
{
    private readonly IEscalationService _escalations;
    private readonly ILogger<SubmitEscalationDecisionCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubmitEscalationDecisionCommandHandler"/> class.</summary>
    /// <param name="escalations">The escalation lifecycle service.</param>
    /// <param name="logger">Logger for recording decision statuses (never reason content).</param>
    public SubmitEscalationDecisionCommandHandler(
        IEscalationService escalations,
        ILogger<SubmitEscalationDecisionCommandHandler> logger)
    {
        _escalations = escalations;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<SubmitEscalationDecisionResult>> Handle(
        SubmitEscalationDecisionCommand request, CancellationToken cancellationToken)
    {
        var decision = new ApproverDecision
        {
            ApproverName = request.ApproverName,
            Approved = request.Approve,
            Reason = request.Reason,
            RespondedAt = DateTimeOffset.UtcNow
        };

        var result = await _escalations.SubmitDecisionAsync(
            request.EscalationId, decision, cancellationToken);

        _logger.LogInformation(
            "Escalation decision: EscalationId={EscalationId}, Approver={ApproverName}, Approve={Approve}, Status={Status}",
            request.EscalationId, request.ApproverName, request.Approve, result.Status);

        if (result.Status == EscalationDecisionStatus.ConflictingDecision)
        {
            return Result<SubmitEscalationDecisionResult>.Conflict(
                "A decision by this approver with the opposite verdict is already recorded; votes cannot be changed.");
        }

        return Result<SubmitEscalationDecisionResult>.Success(
            SubmitEscalationDecisionResult.FromDecisionResult(result));
    }
}
