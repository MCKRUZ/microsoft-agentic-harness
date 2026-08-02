using Domain.AI.Skills;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Single source of truth for how optional agent-level instructions, a set of skill definitions, and
/// optional additional context are merged into the agent's static instruction text.
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
/// Block order: the agent's own instructions (when supplied) lead, ahead of every skill — they are the
/// agent's system prompt. A single skill's instructions are then emitted verbatim; multiple skills are
/// each wrapped in a <c>## Skill: {name}</c> header so the model can tell them apart. Additional context,
/// when present, is appended as a final block. All blocks are joined by a blank line.
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
    /// <param name="agentInstructions">
    /// Optional agent-level instructions (the agent's own system prompt). When present, they lead the
    /// merged text ahead of every skill; ignored when null or whitespace.
    /// </param>
    /// <param name="disclosedOnDemandSkillIds">
    /// Ids of skills whose bodies the framework's <c>load_skill</c> tool will supply on request, and which
    /// are therefore omitted here — the model still sees their name and description in the provider's
    /// index card. Null or empty means every skill's body is emitted, which is the correct behaviour when
    /// no skills provider is wired. Take this from the <see cref="DisclosableSkill.SkillId"/>s that
    /// <see cref="DisclosableSkillFactory.Create"/> returned, and register that same list with the
    /// provider — supplying an id the provider was not given drops those instructions entirely.
    /// </param>
    /// <returns>
    /// The merged instruction text, or an empty string when no agent instructions, skill instructions,
    /// or additional context are supplied.
    /// </returns>
    public static string Merge(
        IReadOnlyList<SkillDefinition> skills,
        string? additionalContext,
        string? agentInstructions = null,
        IReadOnlySet<string>? disclosedOnDemandSkillIds = null)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(agentInstructions))
            parts.Add(agentInstructions);

        foreach (var skill in skills)
        {
            if (string.IsNullOrEmpty(skill.Instructions))
                continue;

            // Tier 2 content the model can pull on demand; keeping it here too would ship the same body
            // twice — once in every prompt, once again if load_skill is called.
            if (disclosedOnDemandSkillIds?.Contains(skill.Id) == true)
                continue;

            // Headed by total skill count, not by how many bodies survive the filter above: when an agent
            // composes several skills the model benefits from the header even if only one body remains.
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
