using Domain.AI.Changes;
using FluentValidation;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Validates <see cref="PreviewAutonomyDecisionQuery"/>: a present, bounded subagent type name;
/// blast radius and target kind values that name defined enum members; and a bounded optional
/// skill key. Whether the subagent type name maps to a defined type is the handler's concern —
/// an unknown subagent is <c>NotFound</c> (404), not a validation failure (400).
/// </summary>
public sealed class PreviewAutonomyDecisionQueryValidator
    : AbstractValidator<PreviewAutonomyDecisionQuery>
{
    /// <summary>Initializes validation rules.</summary>
    public PreviewAutonomyDecisionQueryValidator()
    {
        RuleFor(x => x.SubagentType)
            .NotEmpty().WithMessage("SubagentType must not be empty.")
            .MaximumLength(AutonomyValidationRules.MaxEnumNameLength)
                .WithMessage($"SubagentType must not exceed {AutonomyValidationRules.MaxEnumNameLength} characters.");

        RuleFor(x => x.BlastRadius)
            .NotEmpty().WithMessage("BlastRadius must not be empty.")
            .MaximumLength(AutonomyValidationRules.MaxEnumNameLength)
                .WithMessage($"BlastRadius must not exceed {AutonomyValidationRules.MaxEnumNameLength} characters.")
            .Must(v => AutonomyValidationRules.TryParseEnumName<BlastRadius>(v, out _))
                .When(x => !string.IsNullOrEmpty(x.BlastRadius), ApplyConditionTo.CurrentValidator)
                .WithMessage(AutonomyValidationRules.InvalidBlastRadiusMessage);

        RuleFor(x => x.TargetKind)
            .NotEmpty().WithMessage("TargetKind must not be empty.")
            .MaximumLength(AutonomyValidationRules.MaxEnumNameLength)
                .WithMessage($"TargetKind must not exceed {AutonomyValidationRules.MaxEnumNameLength} characters.")
            .Must(v => AutonomyValidationRules.TryParseEnumName<ChangeTargetKind>(v, out _))
                .When(x => !string.IsNullOrEmpty(x.TargetKind), ApplyConditionTo.CurrentValidator)
                .WithMessage(AutonomyValidationRules.InvalidTargetKindMessage);

        RuleFor(x => x.SkillKey)
            .MaximumLength(AutonomyValidationRules.MaxSkillKeyLength)
                .WithMessage($"SkillKey must not exceed {AutonomyValidationRules.MaxSkillKeyLength} characters.");
    }
}
