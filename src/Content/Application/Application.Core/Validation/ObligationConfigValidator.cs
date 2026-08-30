using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ObligationConfig"/>: the obligation and parallelism ceilings must be
/// positive, and the per-verifier timeout must be a positive duration. Auto-discovered via
/// <c>AddValidatorsFromAssembly</c>, consistent with the sibling config validators.
/// </summary>
public sealed class ObligationConfigValidator : AbstractValidator<ObligationConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ObligationConfigValidator"/> class.</summary>
    public ObligationConfigValidator()
    {
        RuleFor(x => x.MaxObligations)
            .GreaterThan(0).WithMessage("MaxObligations must be > 0.");

        RuleFor(x => x.MaxParallelVerifiers)
            .GreaterThan(0).WithMessage("MaxParallelVerifiers must be > 0.");

        RuleFor(x => x.PerVerifierTimeout)
            .GreaterThan(TimeSpan.Zero).WithMessage("PerVerifierTimeout must be a positive duration.");
    }
}
