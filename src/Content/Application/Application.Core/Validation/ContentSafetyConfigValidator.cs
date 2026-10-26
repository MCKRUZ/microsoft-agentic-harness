using Domain.Common.Config.AI.ContentSafety;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ContentSafetyConfig"/> (#904). The allow-all provider needs nothing. Azure AI Content Safety
/// needs an https endpoint, known categories named once each (none means all four), and a severity threshold on the
/// service's scale.
/// </summary>
/// <remarks>
/// A mistake here would otherwise surface on the first screened request, as every request blocked "because the safety
/// service is unavailable" (the fail-closed default), with the real cause buried in an error log.
/// </remarks>
public sealed class ContentSafetyConfigValidator : AbstractValidator<ContentSafetyConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ContentSafetyConfigValidator"/> class.</summary>
    public ContentSafetyConfigValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();

        When(x => x.Provider == ContentSafetyProvider.AzureAIContentSafety, () =>
        {
            RuleFor(x => x.Endpoint)
                .Must(endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                .WithMessage("ContentSafety:Endpoint must be the https endpoint of the Azure AI Content Safety resource.");

            RuleFor(x => x.Categories)
                .Must(categories => categories.Distinct(StringComparer.Ordinal).Count() == categories.Count)
                .WithMessage("ContentSafety:Categories names a category more than once.");

            RuleForEach(x => x.Categories)
                .Must(category => ContentSafetyConfig.AllCategories.Contains(category, StringComparer.Ordinal))
                .WithMessage(category =>
                    $"ContentSafety:Categories holds an unknown category. Use any of: {string.Join(", ", ContentSafetyConfig.AllCategories)}.");

            RuleFor(x => x.BlockAtSeverity)
                .InclusiveBetween(1, 6)
                .WithMessage("ContentSafety:BlockAtSeverity must be 1 to 6 on the service's 0-6 scale (2 low, 4 medium, 6 high).");
        });
    }
}
