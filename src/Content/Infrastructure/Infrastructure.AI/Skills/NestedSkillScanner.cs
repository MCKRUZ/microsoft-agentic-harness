using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Scans a <c>skills/</c> directory for nested <c>&lt;skill&gt;/SKILL.md</c> files and parses each into a
/// <see cref="SkillDefinition"/>. This is the one shared implementation of the "skills owned under a
/// parent directory" discovery contract, used both by agent discovery (a host agent's
/// <c>&lt;agentDir&gt;/skills/</c>) and by bundle staging (a staged bundle's <c>&lt;bundleDir&gt;/skills/</c>),
/// so the two cannot drift in how they enumerate, which manifest they read, or how they tolerate a
/// malformed entry.
/// </summary>
/// <remarks>
/// Discovery is best-effort and resilient: a missing directory yields an empty list, a directory that
/// cannot be enumerated logs a warning and yields empty, and a single malformed <c>SKILL.md</c> logs a
/// warning and is skipped without aborting the scan of its siblings. Skills whose id could not be
/// resolved are dropped. Callers own de-duplication and any per-skill side effects (registering, caching).
/// </remarks>
internal static class NestedSkillScanner
{
    /// <summary>
    /// Returns the skills found directly under <paramref name="skillsRoot"/> (one per
    /// <c>&lt;subdir&gt;/SKILL.md</c>), in filesystem-enumeration order and possibly containing duplicate
    /// ids if two subdirectories declare the same one — the caller decides how to resolve those.
    /// </summary>
    /// <param name="skillsRoot">The <c>skills/</c> directory to scan. A non-existent path yields an empty list.</param>
    /// <param name="parser">Parser used to read each <c>SKILL.md</c>.</param>
    /// <param name="logger">Logger for enumeration and per-skill parse diagnostics.</param>
    public static IReadOnlyList<SkillDefinition> Scan(string skillsRoot, SkillMetadataParser parser, ILogger logger)
    {
        if (!Directory.Exists(skillsRoot))
            return [];

        IEnumerable<string> skillDirs;
        try
        {
            skillDirs = Directory.EnumerateDirectories(skillsRoot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate nested skills directory: {Path}", skillsRoot);
            return [];
        }

        var skills = new List<SkillDefinition>();
        foreach (var skillDir in skillDirs)
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile))
                continue;

            try
            {
                var skill = parser.ParseFromFile(skillFile, skillDir);
                if (!string.IsNullOrEmpty(skill.Id))
                    skills.Add(skill);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse nested skill from {Path}", skillFile);
            }
        }

        return skills;
    }
}
