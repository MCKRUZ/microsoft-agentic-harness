using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Parses custom frontmatter fields from a raw SKILL.md file path into a <see cref="SkillDefinition"/>.
/// </summary>
/// <remarks>
/// The framework's <c>FileAgentSkillLoader</c> parses only the standard <c>name</c> and
/// <c>description</c> fields. This parser extracts harness-specific fields:
/// <c>category</c>, <c>tags</c>, <c>version</c>, <c>model-override</c>, <c>agent-id</c>,
/// <c>allowed-tools</c>, and <c>skill_type</c>.
/// </remarks>
public sealed class SkillMetadataParser
{
    private readonly ILogger<SkillMetadataParser> _logger;

    public SkillMetadataParser(ILogger<SkillMetadataParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses a SKILL.md file from disk into a <see cref="SkillDefinition"/>.
    /// Extracts both standard fields (name, description) and harness-specific frontmatter.
    /// </summary>
    /// <param name="skillFilePath">Absolute path to the SKILL.md file.</param>
    /// <param name="sourcePath">Directory containing the SKILL.md file (used as <c>BaseDirectory</c>).</param>
    public SkillDefinition ParseFromFile(string skillFilePath, string sourcePath)
    {
        var raw = File.ReadAllText(skillFilePath);
        var frontmatter = ExtractFrontmatter(raw);
        var body = ExtractBody(raw, frontmatter);

        var (objectives, traceFormat, instructions) = ExtractStructuredSections(body);

        var name = ParseString(frontmatter, "name") ?? Path.GetFileName(sourcePath);
        var description = ParseString(frontmatter, "description") ?? string.Empty;

        return new SkillDefinition
        {
            Id = name,
            Name = name,
            Description = description,
            Instructions = instructions,
            Objectives = objectives,
            TraceFormat = traceFormat,
            Category = ParseString(frontmatter, "category"),
            SkillType = ParseString(frontmatter, "skill_type"),
            Version = ParseString(frontmatter, "version"),
            ModelOverride = ParseString(frontmatter, "model-override"),
            AgentId = ParseString(frontmatter, "agent-id"),
            Tags = ParseList(frontmatter, "tags"),
            AllowedTools = ParseList(frontmatter, "allowed-tools"),
            FilePath = skillFilePath,
            BaseDirectory = sourcePath,
            LoadedAt = DateTime.UtcNow,
            IsFullyLoaded = true
        };
    }

    /// <summary>
    /// Builds a <see cref="SkillDefinition"/> from pre-parsed field values (e.g., from the framework's loader).
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="skillDescription">The skill description.</param>
    /// <param name="body">The SKILL.md body content (after frontmatter).</param>
    /// <param name="sourcePath">Directory containing the SKILL.md file.</param>
    public SkillDefinition Parse(string skillName, string? skillDescription, string body, string sourcePath)
    {
        var skillFilePath = Path.Combine(sourcePath, "SKILL.md");
        string? rawFrontmatter = null;

        try
        {
            if (File.Exists(skillFilePath))
            {
                var raw = File.ReadAllText(skillFilePath);
                rawFrontmatter = ExtractFrontmatter(raw);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read custom frontmatter from {Path}", skillFilePath);
        }

        var (objectives, traceFormat, instructions) = ExtractStructuredSections(body);

        return new SkillDefinition
        {
            Id = skillName,
            Name = skillName,
            Description = skillDescription ?? string.Empty,
            Instructions = instructions,
            Objectives = objectives,
            TraceFormat = traceFormat,
            Category = ParseString(rawFrontmatter, "category"),
            SkillType = ParseString(rawFrontmatter, "skill_type"),
            Version = ParseString(rawFrontmatter, "version"),
            ModelOverride = ParseString(rawFrontmatter, "model-override"),
            AgentId = ParseString(rawFrontmatter, "agent-id"),
            Tags = ParseList(rawFrontmatter, "tags"),
            AllowedTools = ParseList(rawFrontmatter, "allowed-tools"),
            FilePath = skillFilePath,
            BaseDirectory = sourcePath,
            LoadedAt = DateTime.UtcNow,
            IsFullyLoaded = true
        };
    }

    private static string? ExtractFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
            return null;

        var end = raw.IndexOf("---", 3, StringComparison.Ordinal);
        return end < 0 ? null : raw[3..end];
    }

    private static string ExtractBody(string raw, string? frontmatter)
    {
        if (frontmatter == null)
            return raw.Trim();

        // Skip the opening ---, frontmatter block, and closing ---
        var closingDelimiter = raw.IndexOf("---", 3, StringComparison.Ordinal);
        if (closingDelimiter < 0)
            return raw.Trim();

        var bodyStart = closingDelimiter + 3;
        return bodyStart >= raw.Length ? string.Empty : raw[bodyStart..].Trim();
    }

    /// <summary>
    /// Extracts Objectives, TraceFormat, and stripped Instructions from a skill body in one pass.
    /// </summary>
    private static (string? Objectives, string? TraceFormat, string Instructions) ExtractStructuredSections(string body)
    {
        return (
            ExtractSection(body, "Objectives"),
            ExtractSection(body, "Trace Format"),
            StripSections(body, "Objectives", "Trace Format")
        );
    }

    /// <summary>
    /// Extracts the content of a named ## Heading section from a markdown body.
    /// Returns null if the heading is not present. Content ends at the next ## heading or EOF.
    /// Matching is case-insensitive; headings inside code fences are ignored.
    /// </summary>
    private static string? ExtractSection(string body, string heading)
    {
        var lines = body.Split('\n');
        var searchHeading = $"## {heading}";

        var startIdx = -1;
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && trimmed.Equals(searchHeading, StringComparison.OrdinalIgnoreCase))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0)
            return null;

        var endIdx = lines.Length;
        inFence = false;
        for (var i = startIdx + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && lines[i].TrimStart().StartsWith("## ", StringComparison.Ordinal))
            {
                endIdx = i;
                break;
            }
        }

        var content = string.Join('\n', lines[(startIdx + 1)..endIdx]).Trim();
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// Returns the body with the specified ## Heading sections removed.
    /// Consecutive blank lines left by removal are collapsed to at most one.
    /// Headings inside code fences are not treated as section boundaries.
    /// </summary>
    private static string StripSections(string body, params string[] headings)
    {
        var headingSet = new HashSet<string>(
            headings.Select(h => $"## {h}"),
            StringComparer.OrdinalIgnoreCase);

        var lines = body.Split('\n');
        var result = new List<string>(lines.Length);
        var skipping = false;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && headingSet.Contains(trimmed))
            {
                skipping = true;
                continue;
            }

            if (!inFence && skipping && line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
                skipping = false;

            if (!skipping)
                result.Add(line);
        }

        // Collapse runs of blank lines to at most one
        var normalized = new List<string>(result.Count);
        var blankRun = 0;
        foreach (var line in result)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun <= 1)
                    normalized.Add(line);
            }
            else
            {
                blankRun = 0;
                normalized.Add(line);
            }
        }

        return string.Join('\n', normalized).Trim();
    }

    private static string? ParseString(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static IList<string> ParseList(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return [];

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            // Inline YAML array: ["a", "b"] or [a, b]
            var rest = trimmed[(key.Length + 1)..].Trim();
            if (rest.StartsWith('['))
            {
                return rest.Trim('[', ']')
                    .Split(',')
                    .Select(s => s.Trim().Trim('"', '\''))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
        }

        return [];
    }
}
