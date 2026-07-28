using FluentValidation;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Validates <see cref="GetAutonomyTierQuery"/>: a present, bounded subagent type name.
/// Whether the name maps to a defined subagent type is the handler's concern — an unknown
/// name is a <c>NotFound</c> (404), not a validation failure (400).
/// </summary>
public sealed class GetAutonomyTierQueryValidator : AbstractValidator<GetAutonomyTierQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public GetAutonomyTierQueryValidator()
    {
        RuleFor(x => x.SubagentType)
            .NotEmpty().WithMessage("SubagentType must not be empty.")
            .MaximumLength(AutonomyValidationRules.MaxEnumNameLength)
                .WithMessage($"SubagentType must not exceed {AutonomyValidationRules.MaxEnumNameLength} characters.");
    }
}
