using Domain.Common.Config.AI.Governance;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="DataClassificationConfig"/>. All rules are conditional on classification being
/// enabled (<see cref="DataClassificationConfig.Mode"/> not <see cref="ClassificationEnforcementMode.Off"/>) —
/// the gate is off by default and a disabled section imposes no constraints, so the template runs out of
/// the box. When enabled the rules ensure the label→action map is coherent: a blank label key can never
/// match a real Purview label and signals a config typo that would silently never fire.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly — no manual
/// registration required.
/// </remarks>
public sealed class DataClassificationConfigValidator : AbstractValidator<DataClassificationConfig>
{
    /// <summary>Initializes a new instance of the <see cref="DataClassificationConfigValidator"/> class.</summary>
    public DataClassificationConfigValidator()
    {
        When(x => x.Mode != ClassificationEnforcementMode.Off, () =>
        {
            RuleFor(x => x.LabelActions)
                .NotNull()
                .WithMessage("LabelActions must not be null when classification is enabled.");

            RuleForEach(x => x.LabelActions)
                .Must(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .WithMessage(
                    "LabelActions contains a blank label name. A blank key can never match a Purview " +
                    "sensitivity label, so the rule would never fire — remove it or supply the label name.");

            RuleFor(x => x.ResultCacheTtl)
                .GreaterThanOrEqualTo(TimeSpan.Zero)
                .WithMessage("ResultCacheTtl cannot be negative; use TimeSpan.Zero to disable result caching.");
        });

        // Information Protection provider rules apply only once it is switched on, independent of Mode:
        // an operator may stage the provider's connection settings before flipping the gate.
        When(x => x.InformationProtection.Enabled, () =>
        {
            RuleFor(x => x.InformationProtection.GraphBaseUrl)
                .Must(BeValidHttpUrl)
                .WithMessage("InformationProtection.GraphBaseUrl must be a valid absolute http(s) URL.");

            RuleFor(x => x.InformationProtection.Scopes)
                .NotEmpty()
                .WithMessage("InformationProtection.Scopes must contain at least one OAuth scope to mint a Graph token.");

            RuleForEach(x => x.InformationProtection.Scopes)
                .Must(scope => !string.IsNullOrWhiteSpace(scope))
                .WithMessage("InformationProtection.Scopes contains a blank scope; remove it or supply a value.");

            RuleFor(x => x.InformationProtection.LabelCatalogCacheTtl)
                .GreaterThan(TimeSpan.Zero)
                .WithMessage("InformationProtection.LabelCatalogCacheTtl must be positive so the label taxonomy is cached.");
        });
    }

    private static bool BeValidHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
