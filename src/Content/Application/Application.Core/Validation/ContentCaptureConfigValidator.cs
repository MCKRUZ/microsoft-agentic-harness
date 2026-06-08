using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.AI.Telemetry;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ContentCaptureConfig"/>. All rules are conditional on
/// <see cref="ContentCaptureConfig.Enabled"/> — content-capture is OFF by
/// default and a disabled section imposes no constraints, so the template runs
/// out of the box. When enabled the rules ensure the redaction posture is
/// coherent: at least one category must be requested and every requested name
/// must map (case-insensitively) to a known
/// <see cref="RedactionCategory"/>. This mirrors the parsing behaviour of the
/// Infrastructure <c>ContentCapturePolicy</c>, so a config typo surfaces both
/// in the options-validation pipeline and (as a debug log) at runtime.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core
/// assembly — no manual registration required.
/// </remarks>
public sealed class ContentCaptureConfigValidator : AbstractValidator<ContentCaptureConfig>
{
    private static readonly HashSet<string> KnownCategories =
        new(Enum.GetNames<RedactionCategory>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ContentCaptureConfigValidator"/> class.</summary>
    public ContentCaptureConfigValidator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.RedactionCategories)
                .NotNull()
                .Must(c => c is { Count: > 0 })
                .WithMessage(
                    "RedactionCategories must contain at least one category when content-capture is enabled. " +
                    "Content can only leave the domain through a redaction rule; an empty list means raw content " +
                    "would be emitted unredacted.");

            RuleForEach(x => x.RedactionCategories)
                .Must(BeKnownCategory)
                .WithMessage(category =>
                    $"RedactionCategories contains '{category}', which is not a known RedactionCategory. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<RedactionCategory>())}.");
        });
    }

    private static bool BeKnownCategory(string category)
        => !string.IsNullOrWhiteSpace(category) && KnownCategories.Contains(category.Trim());
}
