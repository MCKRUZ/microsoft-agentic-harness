using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="GetPendingEscalationsForApproverQuery"/>: the controller-stamped approver
/// name must be present and bounded. An empty name means the principal lacked the configured
/// identity claim — the controller rejects that before dispatch, so this rule is the
/// defense-in-depth backstop.
/// </summary>
public sealed class GetPendingEscalationsForApproverQueryValidator
    : AbstractValidator<GetPendingEscalationsForApproverQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetPendingEscalationsForApproverQueryValidator()
    {
        RuleFor(x => x.ApproverName)
            .NotEmpty().WithMessage("ApproverName must not be empty.")
            .MaximumLength(EscalationValidationRules.MaxApproverNameLength)
                .WithMessage($"ApproverName must not exceed {EscalationValidationRules.MaxApproverNameLength} characters.");
    }
}
