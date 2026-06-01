using Domain.AI.Context;
using Domain.AI.Prompts;

namespace Application.AI.Common.Categorization;

/// <summary>
/// Pure function mapping <see cref="SystemPromptSectionType"/> to the
/// <see cref="ContextCategory"/> that the Foresight context bar groups it under.
/// </summary>
/// <remarks>
/// <para>
/// PR 3 v1 does not yet plumb the section list out of <c>MemoizedPromptComposer</c>,
/// so this map is currently unused at runtime. It is built and tested ahead of
/// time so the follow-up that adds the prompt-composition capture only needs to
/// wire data into <see cref="DefaultContextSnapshotComputer"/> — the mapping is
/// already designed, reviewed, and locked.
/// </para>
/// <para>
/// Unknown / future enum values fall back to <see cref="ContextCategory.System"/>
/// so the wire never carries a phantom bucket; the breakdown stays canonical.
/// </para>
/// </remarks>
public static class SystemPromptSectionCategorizer
{
    /// <summary>
    /// Returns the Foresight category this section type belongs to.
    /// </summary>
    /// <param name="type">A <see cref="SystemPromptSectionType"/> from the prompt composer.</param>
    /// <returns>The category that section's tokens contribute to.</returns>
    public static ContextCategory Map(SystemPromptSectionType type) => type switch
    {
        SystemPromptSectionType.AgentIdentity => ContextCategory.System,
        SystemPromptSectionType.SkillInstructions => ContextCategory.Skills,
        SystemPromptSectionType.ToolSchemas => ContextCategory.Tools,
        SystemPromptSectionType.PermissionRules => ContextCategory.Agents,
        SystemPromptSectionType.GitContext => ContextCategory.System,
        SystemPromptSectionType.UserContext => ContextCategory.Agents,
        SystemPromptSectionType.SessionState => ContextCategory.System,
        SystemPromptSectionType.ActiveHooks => ContextCategory.System,
        SystemPromptSectionType.CustomSection => ContextCategory.System,
        _ => ContextCategory.System,
    };
}
