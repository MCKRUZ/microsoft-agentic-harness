using Domain.Common.Config;

namespace Infrastructure.AI.Skills;

/// <summary>
/// The one place that answers "where does skill content live on disk", for every component that
/// needs to agree on it.
/// </summary>
/// <remarks>
/// <para>
/// Three answers have to stay identical or a real defect appears, and none of them is enforced by
/// the compiler:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>SkillFileReader</c> builds its sandbox from these roots. If it resolved a root differently
///     from the registries, it would refuse the very directory discovery then walks.
///   </description></item>
///   <item><description>
///     <c>SkillMetadataRegistry</c> and <c>AgentMetadataRegistry</c> walk them.
///   </description></item>
///   <item><description>
///     <c>BundleStagingService</c> refuses a staging root that overlaps a discovery root. That guard
///     compares the staging root against these; resolve either side differently and an overlapping
///     root slips past, letting the global registries publish a bundle's private skills.
///   </description></item>
/// </list>
/// <para>
/// Relative paths resolve against <see cref="AppContext.BaseDirectory"/> — the bin folder — rather
/// than the current working directory, matching where the build copies skills and agents. The
/// working directory differs between <c>dotnet run</c> and a published deployment, so anchoring
/// there would make discovery depend on how the host was launched.
/// </para>
/// </remarks>
public static class SkillContentRoots
{
    /// <summary>
    /// The default staging location for expanded bundles when none is configured.
    /// </summary>
    private const string DefaultBundleStagingFolder = "agent-bundles";

    /// <summary>
    /// Resolves a configured path to an absolute one, taking relative paths against
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <param name="path">A configured path, absolute or relative.</param>
    /// <returns>The absolute form.</returns>
    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>
    /// The configured skill and agent discovery roots, resolved to absolute paths.
    /// </summary>
    /// <remarks>
    /// Agent roots are included because an agent's own skills live in
    /// <c>&lt;agentDir&gt;/skills</c> — a location that appears in no skills configuration.
    /// </remarks>
    /// <param name="config">The live application configuration.</param>
    /// <returns>The discovery roots; empty when the AI section is absent.</returns>
    public static IEnumerable<string> Discovery(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var ai = config.AI;
        if (ai is null)
            yield break;

        foreach (var path in ai.Skills.AllPaths.Concat(ai.Agents.AllPaths))
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return Resolve(path);
        }
    }

    /// <summary>
    /// The directory under which each bundle is staged in its own subfolder, resolved to an
    /// absolute path.
    /// </summary>
    /// <param name="config">The live application configuration.</param>
    /// <returns>The configured staging root, or the default under the system temp directory.</returns>
    public static string BundleStaging(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var tempRoot = config.AI?.BundleExecution.TempRoot;
        return string.IsNullOrWhiteSpace(tempRoot)
            ? Path.Combine(Path.GetTempPath(), DefaultBundleStagingFolder)
            : Resolve(tempRoot);
    }

    /// <summary>
    /// Every directory skill content may legitimately be read from: the discovery roots plus the
    /// bundle staging root (a staged bundle carries its own <c>skills/</c> directory).
    /// </summary>
    /// <param name="config">The live application configuration.</param>
    /// <returns>The distinct absolute roots.</returns>
    public static IReadOnlyList<string> All(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.AI is null)
            return [];

        return [.. Discovery(config).Append(BundleStaging(config)).Distinct(StringComparer.Ordinal)];
    }
}
