using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="HumanGateConfig"/> constraints including a non-empty approver roster,
/// required escalation message, valid <see cref="ApprovalStrategy"/> enum value, and positive timeout.
/// </summary>
public sealed class HumanGateConfigValidator : AbstractValidator<HumanGateConfig>
{
    public HumanGateConfigValidator()
    {
        RuleFor(x => x.Approvers)
            .NotEmpty()
            .WithMessage("Approvers is required: a human gate with no approvers can never be approved.");

        RuleFor(x => x.EscalationMessage)
            .NotEmpty()
            .WithMessage("EscalationMessage is required.");

        RuleFor(x => x.ApprovalStrategy)
            .IsInEnum()
            .WithMessage("ApprovalStrategy must be a valid value.");

        RuleFor(x => x.Timeout)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("Timeout must be positive.");
    }
}
