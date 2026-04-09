using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the agent identity section — name, description, and core instructions.
/// This is always the highest-priority section (Priority = 10) and is cacheable
/// because agent identity does not change within a conversation.
/// </summary>
public sealed class AgentIdentitySectionProvider : IPromptSectionProvider
{
    private readonly IAgentExecutionContext _executionContext;

    /// <summary>
    /// Initializes a new instance of <see cref="AgentIdentitySectionProvider"/>.
    /// </summary>
    /// <param name="executionContext">The scoped agent execution context.</param>
    public AgentIdentitySectionProvider(IAgentExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        _executionContext = executionContext;
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.AgentIdentity;

    /// <inheritdoc />
    public Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var agentName = _executionContext.AgentId ?? agentId;

        // Without identity info, we can't produce a meaningful section
        if (string.IsNullOrEmpty(agentName))
            return Task.FromResult<SystemPromptSection?>(null);

        var content = $"You are {agentName}.";

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
