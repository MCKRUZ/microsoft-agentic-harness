namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the filesystem-based skill discovery system.
/// Maps to <c>AppConfig:AI:Skills</c> in appsettings.json.
/// </summary>
public class SkillsConfig
{
    /// <summary>
    /// Gets or sets the primary path to search for SKILL.md files.
    /// Can point to an individual skill folder or a parent folder with skill subdirectories.
    /// Relative paths are resolved from the application's working directory.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Gets or sets additional paths to search beyond <see cref="BasePath"/>.
    /// Useful for loading skills from multiple locations (e.g., built-in + tenant-specific).
    /// </summary>
    public IReadOnlyList<string> AdditionalPaths { get; set; } = [];

    /// <summary>
    /// Gets all configured paths, combining <see cref="BasePath"/> with <see cref="AdditionalPaths"/>.
    /// </summary>
    public IEnumerable<string> AllPaths
    {
        get
        {
            if (!string.IsNullOrEmpty(BasePath))
                yield return BasePath;
            foreach (var path in AdditionalPaths)
                yield return path;
        }
    }
}
