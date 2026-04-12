namespace Domain.Common.MetaHarness;

/// <summary>
/// Immutable, redacted snapshot of a harness configuration at a specific point in time.
/// Used to reproduce and compare candidate harness configurations during optimization.
/// </summary>
public sealed record HarnessSnapshot
{
    /// <summary>
    /// Skill file path → content for the active agent's skill directory only.
    /// Secrets have been removed via the <c>ISecretRedactor</c> before capture.
    /// </summary>
    public required IReadOnlyDictionary<string, string> SkillFileSnapshots { get; init; }

    /// <summary>
    /// System prompt at snapshot time, with secrets redacted.
    /// </summary>
    public required string SystemPromptSnapshot { get; init; }

    /// <summary>
    /// Selected AppConfig key/value pairs as declared in
    /// <c>MetaHarnessConfig.SnapshotConfigKeys</c>, minus any secret keys.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ConfigSnapshot { get; init; }

    /// <summary>
    /// Per-file SHA256 hashes for all entries in <see cref="SkillFileSnapshots"/>.
    /// Enables verification that a snapshot can be faithfully reconstructed.
    /// </summary>
    public required IReadOnlyList<SnapshotEntry> SnapshotManifest { get; init; }
}
