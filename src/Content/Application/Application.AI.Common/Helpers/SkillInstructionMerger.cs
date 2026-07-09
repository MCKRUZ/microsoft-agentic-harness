using Domain.AI.Skills;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Single source of truth for how a set of skill definitions and optional additional context are
/// merged into the agent's static instruction text.
/// </summary>
/// <remarks>
/// <para>
/// Both prompt-building paths flow through this helper so their formatting can never drift apart:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>AgentExecutionContextFactory</c> calls it to build the legacy merged instruction (the
///     default, composer-off path).
///   </description></item>
///   <item><description>
///     The <c>SkillInstructions</c> prompt section (used when the authoritative
///     <c>ISystemPromptComposer</c> is enabled) surfaces exactly this text as its section content.
///   </description></item>
/// </list>
/// <para>
/// A single skill's instructions are emitted verbatim. Multiple skills are each wrapped in a
/// <c>## Skill: {name}</c> header so the model can tell them apart. Additional context, when present,
/// is appended as a final block. All blocks are joined by a blank line.
/// </para>
/// </remarks>
public static class SkillInstructionMerger
{
    /// <summary>
    /// Merges the instructions from <paramref name="skills"/> and the optional
    /// <paramref name="additionalContext"/> into a single instruction string.
    /// </summary>
    /// <param name="skills">The skills whose instructions are merged, in order.</param>
    /// <param name="additionalContext">
    /// Optional extra context appended after all skill instructions; ignored when null or empty.
    /// </param>
    /// <returns>
    /// The merged instruction text, or an empty string when no skill carries instructions and no
    /// additional context is supplied.
    /// </returns>
    public static string Merge(IReadOnlyList<SkillDefinition> skills, string? additionalContext)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var parts = new List<string>();

        foreach (var skill in skills)
        {
            if (string.IsNullOrEmpty(skill.Instructions))
                continue;

            if (skills.Count > 1)
                parts.Add($"## Skill: {skill.Name}\n\n{skill.Instructions}");
            else
                parts.Add(skill.Instructions);
        }

        if (!string.IsNullOrEmpty(additionalContext))
            parts.Add(additionalContext);

        return string.Join("\n\n", parts);
    }
}
