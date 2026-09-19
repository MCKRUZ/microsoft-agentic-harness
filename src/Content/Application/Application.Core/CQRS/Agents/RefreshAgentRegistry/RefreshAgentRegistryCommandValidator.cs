using FluentValidation;

namespace Application.Core.CQRS.Agents.RefreshAgentRegistry;

/// <summary>Validates <see cref="RefreshAgentRegistryCommand"/>: a present, bounded controller-stamped caller identity.</summary>
public sealed class RefreshAgentRegistryCommandValidator : AbstractValidator<RefreshAgentRegistryCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RefreshAgentRegistryCommandValidator()
    {
        RuleFor(x => x.CallerId)
            .NotEmpty().WithMessage("CallerId must not be empty.")
            .MaximumLength(AgentRegistryValidationRules.MaxCallerIdLength)
                .WithMessage($"CallerId must not exceed {AgentRegistryValidationRules.MaxCallerIdLength} characters.");
    }
}
