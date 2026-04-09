namespace Domain.AI.Config;

/// <summary>
/// Represents a configuration file discovered during directory traversal.
/// Files closer to the working directory have higher priority.
/// </summary>
public sealed record DiscoveredConfigFile
{
    /// <summary>Absolute path to the configuration file.</summary>
    public required string FilePath { get; init; }

    /// <summary>The scope/priority level of this file.</summary>
    public required ConfigScope Scope { get; init; }

    /// <summary>Priority within scope — lower values are higher priority. Closer to CWD = lower value.</summary>
    public required int Priority { get; init; }

    /// <summary>The raw content of the configuration file.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional glob patterns from frontmatter that scope this file to specific paths.
    /// Null means the file applies globally.
    /// </summary>
    public IReadOnlyList<string>? PathGlobs { get; init; }
}
