using Domain.Common.Config.AI.HarmonicMemory;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="HarmonicMemoryConfig"/>: the content-length floor must be non-negative and the
/// consolidation fan-out must be positive. Auto-discovered via <c>AddValidatorsFromAssembly</c>,
/// consistent with the sibling config validators (<c>WorkMemoryConfigValidator</c> et al.).
/// </summary>
/// <remarks>
/// <see cref="HarmonicMemoryConfig.Mode"/> needs no rule — the enum's values are exhaustive and
/// <see cref="HarmonicMemoryMode.Off"/> is a valid default. <see cref="HarmonicMemoryConfig.ConsolidationTopK"/>
/// is only consulted in <see cref="HarmonicMemoryMode.Full"/>, but is validated unconditionally so a
/// misconfiguration is caught before the mode is ever raised.
/// </remarks>
public sealed class HarmonicMemoryConfigValidator : AbstractValidator<HarmonicMemoryConfig>
{
    /// <summary>Initializes a new instance of the <see cref="HarmonicMemoryConfigValidator"/> class.</summary>
    public HarmonicMemoryConfigValidator()
    {
        RuleFor(x => x.MinContentLengthChars)
            .GreaterThanOrEqualTo(0).WithMessage("MinContentLengthChars must be >= 0.");

        RuleFor(x => x.ConsolidationTopK)
            .GreaterThan(0).WithMessage("ConsolidationTopK must be > 0.");
    }
}
