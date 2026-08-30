using System.Reflection;
using Application.AI.Common.Interfaces.ClaimVerification;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Verification.Readers;

/// <summary>
/// The <c>"config"</c>-scheme <see cref="ILocatedArtifactReader"/>: resolves a
/// <c>"config:AI.Resilience.Retry.MaxAttempts"</c>-shaped location by walking that dotted path
/// against the live <see cref="AppConfig"/> snapshot via public-property reflection, and returns
/// the live value as evidence text.
/// </summary>
/// <remarks>
/// The first consumer of this scheme is <c>HarnessChangeSuggestion.CurrentValue</c> — a model's
/// claim about what a config field currently holds, checked against what it actually holds via
/// <see cref="Application.AI.Common.Services.SkillTraining.ConfigSurfaceConstraint.ResolveConfigPath"/>.
/// </remarks>
public sealed class ConfigSnapshotLocatedArtifactReader : ILocatedArtifactReader
{
    private const string SchemePrefix = "config:";
    private const BindingFlags PropertyLookup = BindingFlags.Public | BindingFlags.Instance;

    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ConfigSnapshotLocatedArtifactReader> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConfigSnapshotLocatedArtifactReader"/> class.</summary>
    public ConfigSnapshotLocatedArtifactReader(IOptionsMonitor<AppConfig> config, ILogger<ConfigSnapshotLocatedArtifactReader> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string?> TryReadAsync(string location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!location.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            return Task.FromResult<string?>(null);
        }

        var path = location[SchemePrefix.Length..];
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Malformed config location '{Location}' — no path after the 'config:' prefix.", location);
            return Task.FromResult<string?>(null);
        }

        var value = ResolveByPath(_config.CurrentValue, path.Split('.'), path);
        return Task.FromResult(value is null ? null : $"{path} = {value}");
    }

    private object? ResolveByPath(object? current, IReadOnlyList<string> segments, string fullPath)
    {
        foreach (var segment in segments)
        {
            if (current is null)
            {
                return null;
            }

            var property = current.GetType().GetProperty(segment, PropertyLookup);
            if (property is null)
            {
                _logger.LogWarning(
                    "Config location '{FullPath}' does not resolve — '{Segment}' is not a property of '{Type}'.",
                    fullPath, segment, current.GetType().Name);
                return null;
            }

            current = property.GetValue(current);
        }

        return current;
    }
}
