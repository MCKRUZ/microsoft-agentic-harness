namespace Domain.AI.Permissions;

/// <summary>
/// Identifies the origin of a permission rule. Rules from higher-priority sources
/// take precedence during resolution.
/// </summary>
public enum PermissionRuleSource
{
    /// <summary>Rule defined in the agent manifest (AGENT.md).</summary>
    AgentManifest,
    /// <summary>Rule defined in a skill definition (SKILL.md).</summary>
    SkillDefinition,
    /// <summary>Rule from user-level settings.</summary>
    UserSettings,
    /// <summary>Rule from project-level settings.</summary>
    ProjectSettings,
    /// <summary>Rule from local settings (not checked in).</summary>
    LocalSettings,
    /// <summary>Rule from a session-level override (runtime).</summary>
    SessionOverride,
    /// <summary>Rule from organizational policy.</summary>
    PolicySettings,
    /// <summary>Rule from a CLI argument or startup parameter.</summary>
    CliArgument
}
