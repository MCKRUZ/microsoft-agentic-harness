using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="SubmitEscalationDecisionCommand"/>: a non-empty escalation id, a present
/// and bounded controller-stamped approver name, and a bounded optional reason.
/// </summary>
public sealed class SubmitEscalationDecisionCommandValidator
    : AbstractValidator<SubmitEscalationDecisionCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public SubmitEscalationDecisionCommandValidator()
    {
        RuleFor(x => x.EscalationId)
            .NotEmpty().WithMessage("EscalationId must not be empty.");

        RuleFor(x => x.ApproverName)
            .NotEmpty().WithMessage("ApproverName must not be empty.")
            .MaximumLength(EscalationValidationRules.MaxApproverNameLength)
                .WithMessage($"ApproverName must not exceed {EscalationValidationRules.MaxApproverNameLength} characters.");

        RuleFor(x => x.Reason)
            .MaximumLength(EscalationValidationRules.MaxReasonLength)
                .WithMessage($"Reason must not exceed {EscalationValidationRules.MaxReasonLength} characters.");
    }
}
