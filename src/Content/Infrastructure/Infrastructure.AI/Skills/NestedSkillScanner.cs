using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
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
    /// <param name="skillsRoot">
    /// The <c>skills/</c> directory to scan. A non-existent path yields an empty list; a path the
    /// sandbox refuses throws rather than yielding one — see the exception below.
    /// </param>
    /// <param name="parser">Parser used to read each <c>SKILL.md</c>.</param>
    /// <param name="fileReader">
    /// Sandboxed, read-only access to skill content. Both the enumeration and the manifest probe go
    /// through it, confining the scan to the configured skill content roots (issue #247).
    /// </param>
    /// <param name="logger">Logger for enumeration and per-skill parse diagnostics.</param>
    /// <exception cref="SkillPathRefusedException">
    /// <paramref name="skillsRoot"/> lies outside the configured skill content roots. Propagated
    /// rather than absorbed into an empty result, so a misconfigured root fails loudly instead of
    /// producing an agent silently missing its own skills. An ordinary permission denial on an
    /// in-bounds directory is a different type and is still tolerated.
    /// </exception>
    public static IReadOnlyList<SkillDefinition> Scan(
        string skillsRoot, SkillMetadataParser parser, ISkillFileReader fileReader, ILogger logger)
    {
        if (!fileReader.DirectoryExists(skillsRoot))
            return [];

        IReadOnlyList<string> skillDirs;
        try
        {
            skillDirs = fileReader.EnumerateDirectories(skillsRoot);
        }
        catch (Exception ex) when (ex is not SkillPathRefusedException)
        {
            // A sandbox refusal is deliberately NOT caught. Returning an empty list for a refused
            // root reports "this directory holds no skills", which is indistinguishable from a
            // directory that genuinely holds none — so a misconfigured root would silently produce
            // an agent with none of its own skills instead of a startup failure. Best-effort
            // tolerance is for a malformed entry, not for being told the path is out of bounds.
            logger.LogWarning(ex, "Could not enumerate nested skills directory: {Path}", skillsRoot);
            return [];
        }

        var skills = new List<SkillDefinition>();
        foreach (var skillDir in skillDirs)
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!fileReader.FileExists(skillFile))
                continue;

            try
            {
                var skill = parser.ParseFromFile(skillFile, skillDir);
                if (!string.IsNullOrEmpty(skill.Id))
                    skills.Add(skill);
            }
            catch (Exception ex) when (ex is not SkillPathRefusedException)
            {
                // Same rule as the enumeration above: a malformed manifest is skipped, a refused
                // one is not.
                logger.LogWarning(ex, "Failed to parse nested skill from {Path}", skillFile);
            }
        }

        return skills;
    }
}
