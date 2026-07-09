using Domain.Common.Config.AI.ContextManagement;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="PromptCompositionConfig"/> — the section-based system-prompt composition
/// configuration bound from <c>AppConfig:AI:ContextManagement:PromptComposition</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wired into the options pipeline with <c>ValidateOnStart()</c> in the composition root, so an
/// invalid section fails the host at boot rather than at first use.
/// </para>
/// <para>
/// Rules are armed only when <see cref="PromptCompositionConfig.Enabled"/> is <see langword="true"/>,
/// so a default (or omitted) section always passes and hosts that never enable the composer keep
/// booting on class defaults.
/// </para>
/// </remarks>
public sealed class PromptCompositionConfigValidator : AbstractValidator<PromptCompositionConfig>
{
    /// <summary>Initializes a new instance of the <see cref="PromptCompositionConfigValidator"/> class.</summary>
    public PromptCompositionConfigValidator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.TokenBudget)
                .GreaterThan(0)
                .WithMessage(
                    "PromptComposition.TokenBudget must be greater than zero when PromptComposition is " +
                    "enabled — the composer requires a positive budget to assemble sections.");
        });
    }
}
