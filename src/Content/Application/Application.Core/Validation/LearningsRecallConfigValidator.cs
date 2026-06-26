using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="LearningsRecallConfig"/>: the per-turn result cap must be positive and the
/// relevance floor must be a probability. Auto-discovered via <c>AddValidatorsFromAssembly</c>,
/// consistent with the sibling config validators (<c>WorkMemoryConfigValidator</c> et al.).
/// </summary>
public sealed class LearningsRecallConfigValidator : AbstractValidator<LearningsRecallConfig>
{
    /// <summary>Initializes a new instance of the <see cref="LearningsRecallConfigValidator"/> class.</summary>
    public LearningsRecallConfigValidator()
    {
        RuleFor(x => x.MaxResults)
            .GreaterThan(0).WithMessage("MaxResults must be > 0.");

        RuleFor(x => x.MinRelevance)
            .InclusiveBetween(0d, 1d).WithMessage("MinRelevance must be in [0, 1].");
    }
}
