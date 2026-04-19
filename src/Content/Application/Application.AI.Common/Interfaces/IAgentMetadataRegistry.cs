using Domain.AI.Agents;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Registry that discovers and caches agent metadata from filesystem <c>AGENT.md</c> manifests.
/// Provides the identity and categorisation fields required to enumerate or select an agent
/// without loading the full manifest body, tool declarations, or workflow state.
/// </summary>
/// <remarks>
/// The agent analogue of <see cref="ISkillMetadataRegistry"/>. Loaded values are metadata-only
/// (<see cref="AgentDefinition"/>); full manifest parsing — including tool declarations,
/// state configuration, and decision frameworks — is the responsibility of downstream services
/// that consume an individual manifest after it has been selected.
/// </remarks>
public interface IAgentMetadataRegistry
{
    /// <summary>Returns every discovered agent definition.</summary>
    IReadOnlyList<AgentDefinition> GetAll();

    /// <summary>Returns the agent definition for the given id, or <c>null</c> if none is registered.</summary>
    AgentDefinition? TryGet(string agentId);

    /// <summary>Returns agents whose <see cref="AgentDefinition.Category"/> matches the supplied value (case-insensitive).</summary>
    IReadOnlyList<AgentDefinition> GetByCategory(string category);

    /// <summary>Returns agents that carry any of the supplied tags (case-insensitive).</summary>
    IReadOnlyList<AgentDefinition> GetByTags(IEnumerable<string> tags);

    /// <summary>The filesystem paths that were searched during the most recent discovery pass.</summary>
    IReadOnlyList<string> SearchedPaths { get; }
}
