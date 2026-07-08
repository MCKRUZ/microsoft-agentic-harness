using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the agent identity section — name, description, and core instructions.
/// This is always the highest-priority section (Priority = 10) and is cacheable
/// because agent identity does not change within a conversation.
/// </summary>
/// <remarks>
/// The section content is a pure function of the <c>agentId</c> parameter — the
/// same value the singleton <c>IPromptSectionCache</c> keys on. It must NOT be
/// derived from the scoped <c>IAgentExecutionContext</c>: a cacheable section
/// whose content depends on per-scope state but whose cache key does not would
/// let one conversation's identity poison the cached section served to another
/// conversation composing for the same <c>agentId</c>.
/// </remarks>
public sealed class AgentIdentitySectionProvider : IPromptSectionProvider
{
    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.AgentIdentity;

    /// <inheritdoc />
    public Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        // Without identity info, we can't produce a meaningful section
        if (string.IsNullOrEmpty(agentId))
            return Task.FromResult<SystemPromptSection?>(null);

        var content = $"You are {agentId}.";

        var section = new SystemPromptSection(
            Name: "Agent Identity",
            Type: SystemPromptSectionType.AgentIdentity,
            Priority: 10,
            IsCacheable: true,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(content),
            Content: content);

        return Task.FromResult<SystemPromptSection?>(section);
    }
}
