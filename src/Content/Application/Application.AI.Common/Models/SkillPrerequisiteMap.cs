namespace Application.AI.Common.Models;

/// <summary>
/// Prerequisite metadata for all skills in a multi-skill agent context.
/// Stashed in <see cref="Domain.AI.Agents.AgentExecutionContext.AdditionalProperties"/>
/// for the <see cref="Middleware.SkillPrerequisiteMiddleware"/> to consume at runtime.
/// </summary>
public sealed class SkillPrerequisiteMap
{
    /// <summary>
    /// Key used to store/retrieve this map in <see cref="Domain.AI.Agents.AgentExecutionContext.AdditionalProperties"/>.
    /// </summary>
    public const string AdditionalPropertiesKey = "__skillPrerequisites";

    /// <summary>
    /// Prerequisite entries keyed by skill ID.
    /// </summary>
    public required IReadOnlyDictionary<string, SkillPrerequisiteEntry> Skills { get; init; }

    /// <summary>
    /// Whether any skill in the map has prerequisites declared.
    /// </summary>
    public bool HasAnyPrerequisites => Skills.Values.Any(e => e.Prerequisites.Count > 0);
}

/// <summary>
/// Prerequisite and completion metadata for a single skill within a multi-skill agent.
/// </summary>
public sealed class SkillPrerequisiteEntry
{
    /// <summary>
    /// The skill's unique identifier.
    /// </summary>
    public required string SkillId { get; init; }

    /// <summary>
    /// Skill IDs that must complete before this skill's tools are unlocked.
    /// </summary>
    public required IReadOnlyList<string> Prerequisites { get; init; }

    /// <summary>
    /// Tool name whose invocation signals this skill is complete. Null means always complete.
    /// </summary>
    public string? CompletionTool { get; init; }

    /// <summary>
    /// Names of tools that belong to this skill. Used by the middleware to determine
    /// which tools to filter when prerequisites are unmet.
    /// </summary>
    public required IReadOnlyList<string> ToolNames { get; init; }
}
