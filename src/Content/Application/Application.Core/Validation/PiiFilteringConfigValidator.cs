using Application.AI.Common.Services.Compliance;
using Domain.Common.Config.Observability;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="PiiFilteringConfig"/> (#903). An empty <see cref="PiiFilteringConfig.HmacKey"/> is allowed (a
/// random key is made at startup); a set one must be base64 of at least
/// <see cref="HmacErasureSubjectReference.MinimumKeyBytes"/> bytes, the same rule the erasure subject reference applies.
/// </summary>
/// <remarks>
/// The key covers every telemetry pseudonym, so a set but unusable one is a startup error naming the setting. Without this
/// check the mistake would surface only when the telemetry pipeline first built its processors, and in a host that never
/// built them it would not surface at all.
/// </remarks>
public sealed class PiiFilteringConfigValidator : AbstractValidator<PiiFilteringConfig>
{
    /// <summary>Initializes a new instance of the <see cref="PiiFilteringConfigValidator"/> class.</summary>
    public PiiFilteringConfigValidator()
    {
        RuleFor(x => x.HmacKey!)
            .Must(HmacErasureSubjectReference.IsUsableKey)
            .When(x => !string.IsNullOrEmpty(x.HmacKey))
            .WithMessage(
                $"HmacKey must be base64 of at least {HmacErasureSubjectReference.MinimumKeyBytes} bytes, " +
                "or empty to use a random key made at startup.");
    }
}
