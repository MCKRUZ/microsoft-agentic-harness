using Domain.Common.Config.AI;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="Domain.Common.Config.AI.Audit.AuditConfig.ArchivePath"/> against the folders the audit logs
/// live in (#416). Spans several sections, so it validates <see cref="AIConfig"/> rather than one of them.
/// </summary>
/// <remarks>
/// The archive is read alongside the live logs as one chain. If it were the same folder as a log, inside one, or
/// around one, live files would be read as archived ones and the archive's newest file could be a live file; the
/// check that a log's archived part ends exactly where its live part begins would then prove nothing. The receipt
/// folder holds the verification checkpoints and is kept apart for the same reason. Paths are compared after
/// <see cref="Path.GetFullPath(string)"/>, which does not resolve symbolic links, junctions, or a mapped drive versus
/// its UNC path; point the archive at a separate mount.
/// </remarks>
public sealed class AuditArchivePathValidator : AbstractValidator<AIConfig>
{
    /// <summary>Initializes the validator.</summary>
    public AuditArchivePathValidator()
    {
        RuleFor(x => x)
            .Must(config => OverlappingFolder(config) is null)
            .When(config => !string.IsNullOrWhiteSpace(config.Audit.ArchivePath))
            .WithName("AppConfig:AI:Audit:ArchivePath")
            .WithMessage(config =>
                $"Audit:ArchivePath must be a folder of its own; it overlaps {OverlappingFolder(config)}.");
    }

    private static string? OverlappingFolder(AIConfig config)
    {
        var archive = Normalize(config.Audit.ArchivePath);
        return LiveFolders(config)
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .FirstOrDefault(folder => Overlaps(archive, Normalize(folder.Path)))
            .Name;
    }

    /// <summary>Every folder the live audit logs and the verification receipts are written to, with its setting name.</summary>
    internal static IEnumerable<(string Name, string Path)> LiveFolders(AIConfig config) =>
    [
        ("Governance:AuditStoragePath", config.Governance.AuditStoragePath),
        ("Governance:Escalation:AuditStoragePath", config.Governance.Escalation.AuditStoragePath),
        ("Changes:AuditStoragePath", config.Changes.AuditStoragePath),
        ("Egress:AuditStoragePath", config.Egress.AuditStoragePath),
        ("DriftDetection:AuditPath", config.DriftDetection.AuditPath),
        ("Erasure:AuditStoragePath", config.Erasure.AuditStoragePath),
        ("Audit:ReceiptPath", config.Audit.ReceiptPath),
    ];

    /// <summary>Whether two normalized paths are the same, or one is inside the other.</summary>
    internal static bool Overlaps(string first, string second) =>
        IsSameOrInside(first, second) || IsSameOrInside(second, first);

    private static bool IsSameOrInside(string path, string folder) =>
        path.Equals(folder, PathComparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>The full path without a trailing separator.</summary>
    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
