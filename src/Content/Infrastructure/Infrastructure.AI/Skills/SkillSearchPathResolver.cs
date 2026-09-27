using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Resolves <see cref="SkillsConfig.AllPaths"/> into the absolute, existing directories skill
/// discovery should actually scan.
/// </summary>
/// <remarks>
/// Extracted from <c>SkillMetadataRegistry.Discover</c> (issue #709, mirroring
/// <c>Agents.AgentSearchPathResolver</c> from issue #705) so
/// <see cref="BackgroundServices.SkillManifestWatcherService"/> computes the same set of watch
/// roots the registry scans, rather than two independent implementations of "which folders"
/// silently drifting apart.
/// <para>
/// <b>Deliberately routes existence checks through <see cref="ISkillFileReader"/>, not raw
/// <see cref="Directory.Exists(string)"/>.</b> This is NOT the same shape as
/// <c>AgentSearchPathResolver</c>, which uses raw <c>Directory.Exists</c> (a known, separately-
/// tracked gap — issue #710). Skill discovery has always been sandboxed (issue #247): every
/// filesystem check confines the walk to the configured skill roots so it cannot be led outside
/// them. Downgrading this resolver to raw <c>Directory.Exists</c> to look more like the agent
/// side would regress that containment — do not do it.
/// </para>
/// </remarks>
internal static class SkillSearchPathResolver
{
    /// <summary>
    /// Resolves <paramref name="skillsConfig"/>'s configured paths against
    /// <see cref="AppContext.BaseDirectory"/> (via <see cref="SkillContentRoots.Resolve(string)"/> —
    /// this repo's one canonical answer to "resolve a configured path," so this resolver cannot
    /// independently drift from the sandbox or the bundle-overlap guard built on the same roots) and
    /// filters to directories that actually exist, checked through <paramref name="fileReader"/> so
    /// the sandbox boundary is preserved. A configured path that does not exist is logged and
    /// skipped, never treated as an error — a template consumer's unused <c>AdditionalPaths</c> entry
    /// must not break discovery of everything else.
    /// </summary>
    /// <param name="skillsConfig">The configured skill search paths, or <see langword="null"/>.</param>
    /// <param name="fileReader">Sandboxed reader used for the existence check (issue #247).</param>
    /// <param name="logger">Logger for path-resolution diagnostics.</param>
    /// <returns>The absolute, existing directories to scan. Empty when none are configured or none exist.</returns>
    public static IReadOnlyList<string> Resolve(
        SkillsConfig? skillsConfig, ISkillFileReader fileReader, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fileReader);

        var paths = skillsConfig?.AllPaths.ToList() ?? [];
        if (paths.Count == 0)
        {
            logger.LogInformation("No skill paths configured in AppConfig.AI.Skills — skipping skill discovery");
            return [];
        }

        var resolved = new List<string>();
        foreach (var p in paths)
        {
            string abs;
            try
            {
                abs = SkillContentRoots.Resolve(p);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // A malformed AdditionalPaths entry must not abort resolution of every other
                // configured path (mirrors the fix already shipped for AgentSearchPathResolver on
                // #705) — the same "skip and log, don't fail the host" contract every other entry in
                // this loop already gets.
                logger.LogWarning(ex, "Skill path is malformed, skipping: {Path}", p);
                continue;
            }

            if (fileReader.DirectoryExists(abs))
                resolved.Add(abs);
            else
                logger.LogWarning("Skill path not found, skipping: {Path}", abs);
        }

        if (resolved.Count == 0)
            logger.LogWarning("No valid skill paths found — skill discovery produced no results");

        return resolved;
    }
}
