using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.MetaHarness;

/// <summary>
/// Builds a <see cref="HarnessSnapshot"/> from the currently active harness configuration.
/// Secrets are excluded and SHA256 hashes computed for all skill files.
/// </summary>
public interface ISnapshotBuilder
{
    /// <summary>
    /// Captures the active harness state into an immutable, redacted snapshot.
    /// </summary>
    /// <param name="skillDirectory">Absolute path to the agent's skill directory.</param>
    /// <param name="systemPrompt">Current system prompt text (will be redacted).</param>
    /// <param name="configValues">
    /// Key/value pairs from AppConfig to snapshot. Only keys in
    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.SnapshotConfigKeys"/> and not matching any
    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.SecretsRedactionPatterns"/> will be included.
    /// </param>
    /// <param name="cancellationToken"/>
    Task<HarnessSnapshot> BuildAsync(
        string skillDirectory,
        string systemPrompt,
        IReadOnlyDictionary<string, string> configValues,
        CancellationToken cancellationToken = default);
}
