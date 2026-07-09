using System.Collections.Frozen;
using Domain.AI.Prompts;

namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Declares which <see cref="SystemPromptSectionType"/> values make up the agent's authoritative
/// <em>static</em> system prompt when the <c>ISystemPromptComposer</c> path is enabled.
/// </summary>
/// <remarks>
/// <para>
/// The composer can build any registered section, but only these types are baked into the once-built,
/// cached static instruction:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="SystemPromptSectionType.AgentIdentity"/> — who the agent is.</description></item>
///   <item><description><see cref="SystemPromptSectionType.SkillInstructions"/> — the merged skill instructions (the substantive prompt body).</description></item>
///   <item><description><see cref="SystemPromptSectionType.PermissionRules"/> — active approval/deny constraints.</description></item>
/// </list>
/// <para>
/// <see cref="SystemPromptSectionType.ToolSchemas"/> is deliberately excluded — the SDK already sends
/// tool schemas via <c>ChatOptions.Tools</c>, so duplicating them in the prompt wastes budget.
/// <see cref="SystemPromptSectionType.SessionState"/> is deliberately excluded — it is per-turn dynamic
/// state that must not be frozen into a cached static prompt; that need is served on the per-turn
/// <c>AIContextProvider</c> rail. Those two providers remain registered as available building blocks;
/// they simply do not contribute to the authoritative static prompt.
/// </para>
/// </remarks>
public static class AuthoritativePromptSections
{
    /// <summary>
    /// The section types composing the authoritative static system prompt, passed to
    /// <c>ISystemPromptComposer.ComposeAsync</c> to filter out every non-authoritative section.
    /// </summary>
    public static readonly IReadOnlySet<SystemPromptSectionType> Default =
        new[]
        {
            SystemPromptSectionType.AgentIdentity,
            SystemPromptSectionType.SkillInstructions,
            SystemPromptSectionType.PermissionRules,
        }.ToFrozenSet();
}
