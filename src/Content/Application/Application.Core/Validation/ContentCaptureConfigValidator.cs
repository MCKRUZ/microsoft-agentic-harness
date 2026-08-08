using Application.Common.Helpers;
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

    // Shared name-only reader rather than a local set of Enum.GetNames. The summary above promises
    // this validator mirrors ContentCapturePolicy's parsing, and that promise was not being kept:
    // the local set refused "2" while the runtime parse accepted it as a category. Both sides were
    // moved onto this reader together — converting only the validator would have left the divergence
    // exactly where it was, since the runtime side is the one that decides what gets redacted.
    private static bool BeKnownCategory(string category)
        => EnumNameHelper.TryParseName<RedactionCategory>(category, out _);
}
