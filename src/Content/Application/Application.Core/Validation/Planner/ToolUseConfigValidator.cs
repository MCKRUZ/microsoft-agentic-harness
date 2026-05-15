using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="ToolUseConfig"/> constraints including required tool name.
/// Tool key registration is verified at execution time by the step executor.
/// </summary>
public sealed class ToolUseConfigValidator : AbstractValidator<ToolUseConfig>
{
    public ToolUseConfigValidator()
    {
        RuleFor(x => x.ToolName)
            .NotEmpty()
            .WithMessage("ToolName is required.");
    }
}
