using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ClaimVerificationConfig"/>: the claim and parallelism ceilings must be
/// positive, and the per-verifier timeout must be a positive duration. Auto-discovered via
/// <c>AddValidatorsFromAssembly</c>, consistent with the sibling config validators.
/// </summary>
public sealed class ClaimVerificationConfigValidator : AbstractValidator<ClaimVerificationConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ClaimVerificationConfigValidator"/> class.</summary>
    public ClaimVerificationConfigValidator()
    {
        RuleFor(x => x.MaxClaims)
            .GreaterThan(0).WithMessage("MaxClaims must be > 0.");

        RuleFor(x => x.MaxParallelVerifiers)
            .GreaterThan(0).WithMessage("MaxParallelVerifiers must be > 0.");

        RuleFor(x => x.PerVerifierTimeout)
            .GreaterThan(TimeSpan.Zero).WithMessage("PerVerifierTimeout must be a positive duration.");
    }
}
