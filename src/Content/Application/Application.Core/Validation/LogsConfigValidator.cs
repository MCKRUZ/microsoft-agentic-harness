using Application.Common.Helpers;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="LogsConfig"/>. All rules are conditional on
/// <see cref="LogsConfig.OtelExportEnabled"/> — the logs signal is OFF by default,
/// so a host that omits the section (every host but those opting into OTel log
/// export) binds valid defaults and boots unchanged. When export is on the rules
/// keep the pipeline coherent: <see cref="LogsConfig.MinExportLevel"/> must parse
/// to a real <see cref="LogLevel"/>, and — when redaction is on — at least one
/// category must be requested and every requested name must map (case-insensitively)
/// to a known <see cref="RedactionCategory"/>. This mirrors the parsing behaviour of
/// the Infrastructure <c>LogRecordRedactionProcessor</c>, so a config typo surfaces
/// both in the options-validation pipeline and (as a warning) at runtime.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core
/// assembly — no manual registration required. Wired into the startup options
/// pipeline at <c>RegisterValidatedConfigSections</c> with <c>ValidateOnStart</c>.
/// </remarks>
public sealed class LogsConfigValidator : AbstractValidator<LogsConfig>
{
    /// <summary>Initializes a new instance of the <see cref="LogsConfigValidator"/> class.</summary>
    public LogsConfigValidator()
    {
        When(x => x.OtelExportEnabled, () =>
        {
            RuleFor(x => x.MinExportLevel)
                .Must(BeValidLogLevel)
                .WithMessage(x =>
                    $"MinExportLevel '{x.MinExportLevel}' is not a valid LogLevel. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<LogLevel>())}.");

            When(x => x.RedactionEnabled, () =>
            {
                RuleFor(x => x.RedactionCategories)
                    .NotNull()
                    .Must(c => c is { Count: > 0 })
                    .WithMessage(
                        "RedactionCategories must contain at least one category when log redaction is enabled. " +
                        "An empty list would export log content unredacted.");

                RuleForEach(x => x.RedactionCategories)
                    .Must(BeKnownCategory)
                    .WithMessage(category =>
                        $"RedactionCategories contains '{category}', which is not a known RedactionCategory. " +
                        $"Valid values: {string.Join(", ", Enum.GetNames<RedactionCategory>())}.");
            });
        });
    }

    private static bool BeValidLogLevel(string level)
        => !string.IsNullOrWhiteSpace(level)
            && Enum.TryParse<LogLevel>(level.Trim(), ignoreCase: true, out _);

    // Shared name-only reader rather than a local set of Enum.GetNames. The summary above promises
    // this validator mirrors LogRecordRedactionProcessor's parsing, and that promise was not being
    // kept: the local set refused "2" while the runtime parse accepted it as a category. Both sides
    // were moved onto this reader together — the runtime side is the one that decides what gets
    // redacted before logs leave the process, so converting the validator alone would change nothing.
    private static bool BeKnownCategory(string category)
        => EnumNameHelper.TryParseName<RedactionCategory>(category, out _);
}
