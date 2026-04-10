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

        var name = ParseString(frontmatter, "name") ?? Path.GetFileName(sourcePath);
        var description = ParseString(frontmatter, "description") ?? string.Empty;

        return new SkillDefinition
        {
            Id = name,
            Name = name,
            Description = description,
            Instructions = body,
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

        return new SkillDefinition
        {
            Id = skillName,
            Name = skillName,
            Description = skillDescription ?? string.Empty,
            Instructions = body,
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
