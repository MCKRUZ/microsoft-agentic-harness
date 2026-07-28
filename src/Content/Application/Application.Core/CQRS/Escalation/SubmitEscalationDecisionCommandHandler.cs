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
/// documented HTTP status. The exceptions are the two conflict shapes —
/// <see cref="EscalationDecisionStatus.ConflictingDecision"/> (same approver, opposite verdict) and
/// <see cref="EscalationDecisionStatus.AwaitingReconciliation"/> (a verdict was already reached but
/// its durable record could not be written) — which are genuine request conflicts and are translated
/// here to a <see cref="Result{T}.Conflict"/> failure so the shared failure mapper produces the 409.
/// Translating in the Application layer, rather than in a controller, keeps the outcome available to
/// consumers that never touch HTTP.
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

        // Both conflict shapes are translated here rather than at any one transport. The statuses are
        // transport-neutral by design, so a non-HTTP consumer (the console approvals example) would
        // otherwise inherit nothing from an HTTP-only mapping.
        var conflictDetail = result.Status switch
        {
            EscalationDecisionStatus.ConflictingDecision =>
                "A decision by this approver with the opposite verdict is already recorded; " +
                "votes cannot be changed.",

            // A conflict, not transient unavailability: the escalation already reached a verdict
            // before this vote arrived, so the vote was NOT counted and never will be — retrying is
            // guaranteed to produce the same answer. The verdict itself is not lost; reconciliation
            // re-drives it.
            EscalationDecisionStatus.AwaitingReconciliation =>
                "The escalation had already reached a verdict whose durable record could not be " +
                "written, so it is parked awaiting reconciliation. This decision was not counted " +
                "and will not be; retrying it will not change that. Poll the escalation by id for " +
                "the final outcome.",

            _ => null
        };

        return conflictDetail is not null
            ? Result<SubmitEscalationDecisionResult>.Conflict(conflictDetail)
            : Result<SubmitEscalationDecisionResult>.Success(
                SubmitEscalationDecisionResult.FromDecisionResult(result));
    }
}
