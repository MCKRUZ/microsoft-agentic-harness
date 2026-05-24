using System.Text.Json;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Reads and validates plugin.json manifests from plugin directories.
/// </summary>
public sealed class PluginManifestReader : IPluginManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<PluginManifestReader> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PluginManifestReader"/>.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PluginManifestReader(ILogger<PluginManifestReader> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public PluginManifest? Read(string pluginDirectory)
    {
        // Canonicalize the base directory to prevent path traversal via ".." segments
        // in the pluginDirectory argument before constructing any file paths from it.
        string canonicalBase;
        try
        {
            canonicalBase = Path.GetFullPath(pluginDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid plugin directory path: {Directory}", pluginDirectory);
            return null;
        }

        if (!Directory.Exists(canonicalBase))
        {
            _logger.LogDebug("Plugin directory does not exist: {Directory}", canonicalBase);
            return null;
        }

        // Resolve the manifest path and verify it is still inside the canonical base.
        var manifestPath = Path.GetFullPath(Path.Combine(canonicalBase, "plugin.json"));
        if (!manifestPath.StartsWith(canonicalBase + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && manifestPath != canonicalBase)
        {
            _logger.LogWarning(
                "Resolved manifest path {ManifestPath} escapes plugin directory {Base}",
                manifestPath, canonicalBase);
            return null;
        }

        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug("No plugin.json found at {Path}", manifestPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

            if (manifest is null || string.IsNullOrEmpty(manifest.Name))
            {
                _logger.LogWarning("Invalid plugin manifest at {Path}: missing name", manifestPath);
                return null;
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse plugin.json at {Path}", manifestPath);
            return null;
        }
    }
}
