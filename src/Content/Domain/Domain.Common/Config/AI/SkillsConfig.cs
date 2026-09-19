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

    /// <summary>
    /// Whether the skill registry watches its configured paths for filesystem changes and
    /// automatically reloads — the default experience for a template consumer, so an added, edited,
    /// or removed <c>SKILL.md</c> becomes visible without a process restart (issue #709, mirroring
    /// <see cref="AgentsConfig.WatchForChanges"/> from issue #705). Set to <see langword="false"/> in
    /// a deployment where skill directories are baked into a container image and never change at
    /// runtime, to avoid holding a filesystem watch handle per configured root for no benefit.
    /// </summary>
    public bool WatchForChanges { get; set; } = true;

    /// <summary>
    /// How long the watcher waits for filesystem writes to settle before reloading, after seeing the
    /// first change in a burst. A single manifest edit produces several filesystem events in quick
    /// succession (most editors write via a temp file plus rename); without this quiet period, the
    /// first event could trigger a reload that reads a half-written file. Clamped defensively to a
    /// sane range by the watcher rather than validated by a dedicated options validator — two bounded
    /// numbers do not warrant new options-pipeline machinery.
    /// </summary>
    public int ChangeDebounceMilliseconds { get; set; } = 500;
}
