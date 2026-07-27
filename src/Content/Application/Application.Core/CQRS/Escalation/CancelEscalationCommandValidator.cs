using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="CancelEscalationCommand"/>: a non-empty escalation id, a required bounded
/// reason (cancellations are administrative actions — an unexplained one is not auditable), and a
/// present, bounded controller-stamped canceller identity.
/// </summary>
public sealed class CancelEscalationCommandValidator : AbstractValidator<CancelEscalationCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public CancelEscalationCommandValidator()
    {
        RuleFor(x => x.EscalationId)
            .NotEmpty().WithMessage("EscalationId must not be empty.");

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason must not be empty — cancellations must be explained for the audit trail.")
            .MaximumLength(EscalationValidationRules.MaxReasonLength)
                .WithMessage($"Reason must not exceed {EscalationValidationRules.MaxReasonLength} characters.");

        RuleFor(x => x.CancelledBy)
            .NotEmpty().WithMessage("CancelledBy must not be empty.")
            .MaximumLength(EscalationValidationRules.MaxApproverNameLength)
                .WithMessage($"CancelledBy must not exceed {EscalationValidationRules.MaxApproverNameLength} characters.");
    }
}
