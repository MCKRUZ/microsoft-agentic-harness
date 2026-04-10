using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Factory for creating configured AI agents with observability, middleware, and tool support.
/// </summary>
public interface IAgentFactory
{
    /// <summary>
    /// Creates a fully configured AI agent from an execution context.
    /// </summary>
    /// <param name="agentContext">Runtime configuration including instructions, tools, and middleware.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured <see cref="AIAgent"/> ready for use.</returns>
    Task<AIAgent> CreateAgentAsync(AgentExecutionContext agentContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new persistent agent in Azure AI Foundry and returns the configured AIAgent with its assigned ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method provisions a new agent on the AI Foundry server, sets the returned agent ID
    /// on the execution context, and then delegates to <see cref="CreateAgentAsync"/> for full
    /// middleware pipeline configuration (OTel, caching, function invocation, diagnostics).
    /// </para>
    /// <para>
    /// Requires <c>AppConfig.AI.AIFoundry.IsConfigured</c> to be true and
    /// <c>PersistentAgentsClient</c> to be registered in DI.
    /// </para>
    /// </remarks>
    /// <param name="agentContext">
    /// Runtime configuration for the agent to create. The <see cref="AgentExecutionContext.AgentId"/>
    /// and <see cref="AgentExecutionContext.AIAgentFrameworkType"/> properties will be set by this method.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the configured <see cref="AIAgent"/> and the assigned agent ID.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>PersistentAgentsClient</c> is not configured in DI.
    /// </exception>
    Task<(AIAgent Agent, string AgentId)> CreatePersistentAgentAsync(
        AgentExecutionContext agentContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an AI agent by loading a skill definition.
    /// </summary>
    /// <param name="skillId">The unique identifier of the skill to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AIAgent> CreateAgentFromSkillAsync(string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an AI agent from a skill with custom options.
    /// </summary>
    /// <param name="skillId">The unique identifier of the skill to load.</param>
    /// <param name="options">Configuration for resource loading and agent overrides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AIAgent> CreateAgentFromSkillAsync(string skillId, SkillAgentOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates agents for multiple skill IDs in a single call.
    /// Skills that fail to load are logged and skipped; the returned dictionary contains only successful agents.
    /// </summary>
    /// <param name="skillIds">The skill identifiers to load.</param>
    /// <param name="options">Optional configuration applied to all agents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IDictionary<string, AIAgent>> CreateAgentsFromSkillsAsync(IEnumerable<string> skillIds, SkillAgentOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates multiple agents by discovering skills in a category.
    /// </summary>
    Task<IDictionary<string, AIAgent>> CreateAgentsByCategoryAsync(string category, SkillAgentOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates multiple agents by discovering skills with matching tags.
    /// </summary>
    Task<IDictionary<string, AIAgent>> CreateAgentsByTagsAsync(IEnumerable<string> tags, SkillAgentOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a specific AI provider is configured and available.
    /// </summary>
    bool IsProviderAvailable(AIAgentFrameworkClientType clientType);

    /// <summary>
    /// Gets availability status for all AI providers.
    /// </summary>
    IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders();
}
