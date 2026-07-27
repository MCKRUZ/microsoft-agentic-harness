using FluentValidation;

namespace Application.Core.CQRS.Escalation;

/// <summary>
/// Validates <see cref="GetEscalationQuery"/>: a non-empty escalation id and a present, bounded
/// controller-stamped approver name.
/// </summary>
public sealed class GetEscalationQueryValidator : AbstractValidator<GetEscalationQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetEscalationQueryValidator()
    {
        RuleFor(x => x.EscalationId)
            .NotEmpty().WithMessage("EscalationId must not be empty.");

        RuleFor(x => x.ApproverName)
            .NotEmpty().WithMessage("ApproverName must not be empty.")
            .MaximumLength(EscalationValidationRules.MaxApproverNameLength)
                .WithMessage($"ApproverName must not exceed {EscalationValidationRules.MaxApproverNameLength} characters.");
    }
}
