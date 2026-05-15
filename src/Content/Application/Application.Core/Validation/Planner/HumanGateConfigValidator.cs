using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="HumanGateConfig"/> constraints including required escalation message,
/// valid <see cref="ApprovalStrategy"/> enum value, and positive timeout.
/// </summary>
public sealed class HumanGateConfigValidator : AbstractValidator<HumanGateConfig>
{
    public HumanGateConfigValidator()
    {
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
