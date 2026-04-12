namespace Domain.Common.MetaHarness;

/// <summary>
/// An entry in a <see cref="HarnessSnapshot.SnapshotManifest"/> recording the SHA256
/// hash of a single skill file for reproducibility verification.
/// </summary>
public sealed record SnapshotEntry(
    /// <summary>Relative skill file path (e.g., "skills/research-agent/SKILL.md").</summary>
    string Path,
    /// <summary>Lowercase hex SHA256 hash of the file contents at snapshot time.</summary>
    string Sha256Hash);
