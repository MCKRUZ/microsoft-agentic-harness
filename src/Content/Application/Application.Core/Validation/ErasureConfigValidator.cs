using Application.AI.Common.Services.Compliance;
using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ErasureConfig"/>. An empty <see cref="ErasureConfig.SubjectReferenceKey"/> is allowed (no subject
/// reference is recorded); a set one must be base64 of at least <see cref="HmacErasureSubjectReference.MinimumKeyBytes"/>
/// bytes. Operator tenant ids must be real ids.
/// </summary>
/// <remarks>
/// Without this, a mistyped or short key would surface on the first erasure instead of at startup, and a whitespace-only
/// key would decode to nothing and make the reference a plain hash anyone could reverse.
/// </remarks>
public sealed class ErasureConfigValidator : AbstractValidator<ErasureConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ErasureConfigValidator"/> class.</summary>
    public ErasureConfigValidator()
    {
        RuleFor(x => x.SubjectReferenceKey)
            .Must(HmacErasureSubjectReference.IsUsableKey)
            .When(x => !string.IsNullOrEmpty(x.SubjectReferenceKey))
            .WithMessage(
                $"SubjectReferenceKey must be base64 of at least {HmacErasureSubjectReference.MinimumKeyBytes} bytes, " +
                "or empty to record no subject reference.");

        RuleForEach(x => x.OperatorTenantIds)
            .Must(id => !string.IsNullOrWhiteSpace(id) && id == id.Trim())
            .WithMessage("OperatorTenantIds entries must be tenant ids with no surrounding whitespace.");
    }
}
