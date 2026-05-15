using Domain.AI.Planner;
using FluentValidation;

namespace Application.Core.Validation.Planner;

/// <summary>
/// Validates <see cref="LlmCallConfig"/> constraints including required deployment key,
/// temperature range (0.0–2.0), and positive maximum token limit.
/// </summary>
public sealed class LlmCallConfigValidator : AbstractValidator<LlmCallConfig>
{
    public LlmCallConfigValidator()
    {
        RuleFor(x => x.ModelDeploymentKey)
            .NotEmpty()
            .WithMessage("ModelDeploymentKey is required.");

        RuleFor(x => x.Temperature)
            .InclusiveBetween(0.0, 2.0)
            .WithMessage("Temperature must be between 0 and 2.");

        RuleFor(x => x.MaxTokens)
            .GreaterThan(0)
            .WithMessage("MaxTokens must be greater than 0.");
    }
}
