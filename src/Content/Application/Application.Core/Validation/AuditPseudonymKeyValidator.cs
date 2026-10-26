using Application.AI.Common.Services.Compliance;
using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates the audit pseudonym key settings (#903): a configured <c>Audit:PseudonymKey</c> must be base64 of at least
/// <see cref="HmacErasureSubjectReference.MinimumKeyBytes"/> bytes, and the key file must sit outside every audit log
/// folder, the receipt folder and the archive.
/// </summary>
/// <remarks>
/// The key turns every person reference in the audit trails back into something checkable, so it must not travel with
/// them: a key file inside a log folder would be copied into the archive with the day-files, and handed out with them
/// wherever a log folder is shared. Spans several sections, so it validates <see cref="AIConfig"/>, like
/// <see cref="AuditArchivePathValidator"/>, whose folder list it shares.
/// </remarks>
public sealed class AuditPseudonymKeyValidator : AbstractValidator<AIConfig>
{
    /// <summary>Initializes the validator.</summary>
    public AuditPseudonymKeyValidator()
    {
        RuleFor(x => x.Audit.PseudonymKey)
            .Must(HmacErasureSubjectReference.IsUsableKey)
            .When(x => !string.IsNullOrEmpty(x.Audit.PseudonymKey))
            .WithName("AppConfig:AI:Audit:PseudonymKey")
            .WithMessage(
                $"Audit:PseudonymKey must be base64 of at least {HmacErasureSubjectReference.MinimumKeyBytes} bytes, " +
                "or empty to use the key file.");

        RuleFor(x => x.Audit.PseudonymKeyPath)
            .NotEmpty()
            .When(x => string.IsNullOrEmpty(x.Audit.PseudonymKey))
            .WithName("AppConfig:AI:Audit:PseudonymKeyPath")
            .WithMessage("Audit:PseudonymKeyPath must name the key file when Audit:PseudonymKey is empty.");

        RuleFor(x => x)
            .Must(config => OverlappingFolder(config) is null)
            .When(config => string.IsNullOrEmpty(config.Audit.PseudonymKey)
                && !string.IsNullOrWhiteSpace(config.Audit.PseudonymKeyPath))
            .WithName("AppConfig:AI:Audit:PseudonymKeyPath")
            .WithMessage(config =>
                $"Audit:PseudonymKeyPath must be outside the audit folders; it is inside {OverlappingFolder(config)}.");
    }

    private static string? OverlappingFolder(AIConfig config)
    {
        var keyFolder = AuditArchivePathValidator.Normalize(
            Path.GetDirectoryName(Path.GetFullPath(config.Audit.PseudonymKeyPath))!);
        var protectedFolders = AuditArchivePathValidator.LiveFolders(config)
            .Append((Name: "Audit:ArchivePath", Path: config.Audit.ArchivePath));
        return protectedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .FirstOrDefault(folder => AuditArchivePathValidator.Overlaps(
                keyFolder, AuditArchivePathValidator.Normalize(folder.Path)))
            .Name;
    }
}
