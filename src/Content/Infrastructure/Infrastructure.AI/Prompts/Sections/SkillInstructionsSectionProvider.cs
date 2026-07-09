using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the skill-instructions section — the substantive body of the agent's static system
/// prompt. Its content is the merged skill instructions (plus any additional context) produced by
/// <see cref="SkillInstructionMerger"/>, sourced from the scoped <see cref="ISkillInstructionAccessor"/>
/// that <c>AgentExecutionContextFactory</c> stamps before composing.
/// </summary>
/// <remarks>
/// <para>
/// Priority 20 places this section immediately after agent identity (10) and before permission rules
/// (40), matching the position of <see cref="SystemPromptSectionType.SkillInstructions"/> in the
/// section-type ordering.
/// </para>
/// <para>
/// Not cacheable. The content derives from per-request scoped state (the accessor), whereas the
/// composer's memoization cache is keyed only by <c>agentId</c>. Caching a scope-derived section under
/// an agent-id-only key is the exact poisoning hazard documented on
/// <c>AgentIdentitySectionProvider</c> — one request's additional context could bleed into another
/// request composing for the same agent. Recomputing this section per composition is cheap (a string
/// join and a token estimate) and composition happens roughly once per conversation, so the section is
/// intentionally recomputed rather than cached.
/// </para>
/// </remarks>
public sealed class SkillInstructionsSectionProvider : IPromptSectionProvider
{
    private readonly ISkillInstructionAccessor _accessor;

    /// <summary>
    /// Initializes a new instance of <see cref="SkillInstructionsSectionProvider"/>.
    /// </summary>
    /// <param name="accessor">The scoped holder carrying the current request's merged skill instructions.</param>
    public SkillInstructionsSectionProvider(ISkillInstructionAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.SkillInstructions;

    /// <inheritdoc />
    public Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var instructions = _accessor.Instructions;

        // No skill instructions stamped for this scope — yield no content.
        if (string.IsNullOrEmpty(instructions))
            return Task.FromResult<SystemPromptSection?>(null);

        var section = new SystemPromptSection(
            Name: "Skill Instructions",
            Type: SystemPromptSectionType.SkillInstructions,
            Priority: 20,
            IsCacheable: false,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(instructions),
            Content: instructions);

        return Task.FromResult<SystemPromptSection?>(section);
    }
}
