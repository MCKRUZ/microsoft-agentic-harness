using System.Text.Json.Serialization;

namespace Domain.Common.Config.AI.Plugins;

/// <summary>
/// Deserialized from plugin.json — compatible with Microsoft's azure-skills format.
/// </summary>
public class PluginManifest
{
    /// <summary>Plugin display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description of the plugin.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Plugin version (semver).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Plugin author information.</summary>
    public PluginAuthor? Author { get; set; }

    /// <summary>Plugin homepage URL.</summary>
    public string? Homepage { get; set; }

    /// <summary>Source repository URL.</summary>
    public string? Repository { get; set; }

    /// <summary>License identifier (e.g., "MIT").</summary>
    public string? License { get; set; }

    /// <summary>Searchable keywords.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>Relative path to skills directory (e.g., "./skills/").</summary>
    public string? Skills { get; set; }

    /// <summary>Relative path to MCP config file (e.g., "./.mcp.json").</summary>
    public string? McpServers { get; set; }

    /// <summary>Hook configuration.</summary>
    public PluginHooksManifest? Hooks { get; set; }
}

/// <summary>Plugin author with optional URL.</summary>
public record PluginAuthor
{
    /// <summary>Author display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Author homepage or profile URL.</summary>
    public string? Url { get; init; }
}

/// <summary>Hook configuration from plugin.json.</summary>
public class PluginHooksManifest
{
    /// <summary>Relative paths to hook scripts.</summary>
    public IReadOnlyList<string> Paths { get; set; } = [];

    /// <summary>Whether plugin hooks replace existing hooks of the same type.</summary>
    public bool Exclusive { get; set; }
}
