namespace Domain.AI.Skills;

/// <summary>
/// Well-known skill source identifiers used in <c>SkillNotFoundException</c>
/// and skill loading diagnostics. Use these constants instead of raw strings
/// to prevent typos and enable refactoring.
/// </summary>
/// <example>
/// <code>
/// var skill = registry.TryGet(skillId)
///     ?? throw new SkillNotFoundException(skillId, SkillSources.Filesystem);
/// </code>
/// </example>
public static class SkillSources
{
    /// <summary>Skill is embedded in an application assembly as a resource.</summary>
    public const string Bundled = "bundled";

    /// <summary>Skill is loaded from the filesystem (SKILL.md file).</summary>
    public const string Filesystem = "filesystem";

    /// <summary>Skill is provided by an MCP server at runtime.</summary>
    public const string Mcp = "mcp";

    /// <summary>Skill is loaded from an external plugin or extension point.</summary>
    public const string Plugin = "plugin";

    /// <summary>Skill is defined inline in code (e.g., in an agent manifest).</summary>
    public const string Inline = "inline";
}
