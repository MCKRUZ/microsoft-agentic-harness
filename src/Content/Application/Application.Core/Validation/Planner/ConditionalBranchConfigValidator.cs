using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="ConditionalBranchConfig"/> constraints including required condition
/// expression and valid true/false edge target step identifiers.
/// </summary>
public sealed class ConditionalBranchConfigValidator : AbstractValidator<ConditionalBranchConfig>
{
    public ConditionalBranchConfigValidator()
    {
        RuleFor(x => x.ConditionExpression)
            .NotEmpty()
            .WithMessage("ConditionExpression is required.");

        RuleFor(x => x.TrueEdgeTargetId.Value)
            .NotEqual(Guid.Empty)
            .WithMessage("TrueEdgeTargetId must reference a valid step.");

        RuleFor(x => x.FalseEdgeTargetId.Value)
            .NotEqual(Guid.Empty)
            .WithMessage("FalseEdgeTargetId must reference a valid step.");
    }
}
