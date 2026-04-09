using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the skill instructions section by delegating to <see cref="ITieredContextAssembler"/>
/// to load Tier 2 instructions for discovered skills. Not cacheable because the active
/// skill set can change per turn.
/// </summary>
public sealed class SkillInstructionsSectionProvider : IPromptSectionProvider
{
    private readonly ITieredContextAssembler _contextAssembler;
    private readonly ISkillLoaderService _skillLoader;
    private readonly ILogger<SkillInstructionsSectionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SkillInstructionsSectionProvider"/>.
    /// </summary>
    /// <param name="contextAssembler">Assembles tiered context from skill definitions.</param>
    /// <param name="skillLoader">Discovers and loads skill definitions.</param>
    /// <param name="logger">Logger instance.</param>
    public SkillInstructionsSectionProvider(
        ITieredContextAssembler contextAssembler,
        ISkillLoaderService skillLoader,
        ILogger<SkillInstructionsSectionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(skillLoader);
        ArgumentNullException.ThrowIfNull(logger);

        _contextAssembler = contextAssembler;
        _skillLoader = skillLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.SkillInstructions;

    /// <inheritdoc />
    public async Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var skills = await _skillLoader.DiscoverSkillsAsync(cancellationToken: cancellationToken);
        if (skills.Count == 0)
            return null;

        var instructionParts = new List<string>();

        foreach (var skill in skills)
        {
            try
            {
                var assembled = await _contextAssembler.AssembleContextAsync(
                    skill, basePath: null, cancellationToken);

                if (!string.IsNullOrWhiteSpace(assembled.FormattedPromptSection))
                {
                    instructionParts.Add($"## Skill: {skill.Name}\n{assembled.FormattedPromptSection}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to assemble context for skill {SkillId}", skill.Id);
            }
        }

        if (instructionParts.Count == 0)
            return null;

        var content = string.Join("\n\n", instructionParts);

        return new SystemPromptSection(
            Name: "Skill Instructions",
            Type: SystemPromptSectionType.SkillInstructions,
            Priority: 20,
            IsCacheable: false,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(content),
            Content: content);
    }
}
