namespace Domain.AI.Prompts;

/// <summary>
/// Identifies the type of a system prompt section. Each section type has a dedicated
/// provider that computes its content. Sections are composed in priority order to
/// build the full system prompt.
/// </summary>
public enum SystemPromptSectionType
{
    /// <summary>Agent identity — name, role, description, core instructions.</summary>
    AgentIdentity,

    /// <summary>Skill-specific instructions loaded via progressive disclosure.</summary>
    SkillInstructions,

    /// <summary>Available tool names and descriptions for agent awareness.</summary>
    ToolSchemas,

    /// <summary>Active permission rules expressed as natural language constraints.</summary>
    PermissionRules,

    /// <summary>Git repository context — branch, status, recent commits.</summary>
    GitContext,

    /// <summary>User-provided context — CLAUDE.md, project notes.</summary>
    UserContext,

    /// <summary>Session state — turn count, budget remaining, active skills.</summary>
    SessionState,

    /// <summary>Active hook registrations for agent awareness.</summary>
    ActiveHooks,

    /// <summary>Custom/extension section type for plugin-provided content.</summary>
    CustomSection
}
