namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the filesystem-based agent manifest discovery system.
/// Maps to <c>AppConfig:AI:Agents</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SkillsConfig"/> in shape. Agents and skills are kept on separate paths
/// because they are distinct concepts: agents are top-level orchestrators declared by <c>AGENT.md</c>,
/// while skills are procedural units declared by <c>SKILL.md</c>.
/// </remarks>
public class AgentsConfig
{
    /// <summary>
    /// Primary path to search for <c>AGENT.md</c> files. May point to a single agent folder
    /// or a parent folder containing multiple agent subdirectories. Relative paths are resolved
    /// from the application's working directory.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Additional paths to search beyond <see cref="BasePath"/>. Useful for combining built-in
    /// agents with tenant- or environment-specific agents at runtime.
    /// </summary>
    public IReadOnlyList<string> AdditionalPaths { get; set; } = [];

    /// <summary>
    /// All configured paths, <see cref="BasePath"/> first followed by <see cref="AdditionalPaths"/>.
    /// Skips <see cref="BasePath"/> when it is null or empty.
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
    /// Whether the agent registry watches its configured paths for filesystem changes and
    /// automatically reloads — the default experience for a template consumer, so an added, edited,
    /// or removed <c>AGENT.md</c> becomes visible without a process restart (issue #705). Set to
    /// <see langword="false"/> in a deployment where agent directories are baked into a container
    /// image and never change at runtime, to avoid holding a filesystem watch handle per configured
    /// root for no benefit.
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
