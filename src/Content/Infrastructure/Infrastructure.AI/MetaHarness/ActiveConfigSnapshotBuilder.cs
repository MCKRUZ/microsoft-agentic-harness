using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Builds a <see cref="HarnessSnapshot"/> from the live filesystem and configuration.
/// Applies <see cref="ISecretRedactor"/> to all content before capture.
/// </summary>
public sealed class ActiveConfigSnapshotBuilder : ISnapshotBuilder
{
    private readonly IOptionsMonitor<MetaHarnessConfig> _options;
    private readonly ISecretRedactor _redactor;

    /// <summary>
    /// Initializes a new instance of <see cref="ActiveConfigSnapshotBuilder"/>.
    /// </summary>
    public ActiveConfigSnapshotBuilder(
        IOptionsMonitor<MetaHarnessConfig> options,
        ISecretRedactor redactor)
    {
        _options = options;
        _redactor = redactor;
    }

    /// <inheritdoc/>
    public async Task<HarnessSnapshot> BuildAsync(
        string skillDirectory,
        string systemPrompt,
        IReadOnlyDictionary<string, string> configValues,
        CancellationToken cancellationToken = default)
    {
        var config = _options.CurrentValue;
        var skillFiles = await EnumerateSkillFilesAsync(skillDirectory, cancellationToken);

        // Redact each skill file's content before storing; hash the redacted content for self-consistent verification.
        var redactedSkillFiles = skillFiles.ToDictionary(
            kvp => kvp.Key,
            kvp => _redactor.Redact(kvp.Value) ?? string.Empty);

        return new HarnessSnapshot
        {
            SkillFileSnapshots = redactedSkillFiles,
            SystemPromptSnapshot = _redactor.Redact(systemPrompt) ?? string.Empty,
            ConfigSnapshot = BuildConfigSnapshot(config, configValues),
            SnapshotManifest = redactedSkillFiles
                .Select(kvp => new SnapshotEntry(
                    kvp.Key,
                    ComputeSha256Hex(Encoding.UTF8.GetBytes(kvp.Value))))
                .ToList()
        };
    }

    private static async Task<Dictionary<string, string>> EnumerateSkillFilesAsync(
        string skillDirectory,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(skillDirectory))
            return result;

        foreach (var filePath in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(skillDirectory, filePath)
                .Replace('\\', '/');
            result[relativePath] = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        }

        return result;
    }

    private IReadOnlyDictionary<string, string> BuildConfigSnapshot(
        MetaHarnessConfig config,
        IReadOnlyDictionary<string, string> configValues)
    {
        var result = new Dictionary<string, string>();

        foreach (var key in config.SnapshotConfigKeys)
        {
            if (_redactor.IsSecretKey(key))
                continue;

            if (configValues.TryGetValue(key, out var value))
                result[key] = value;
        }

        return result;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
