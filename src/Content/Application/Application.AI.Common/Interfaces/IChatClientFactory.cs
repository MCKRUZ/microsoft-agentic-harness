using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Unified factory for creating <see cref="IChatClient"/> instances from configured AI services.
/// Supports Azure OpenAI, OpenAI, and AI Foundry persistent agents.
/// </summary>
public interface IChatClientFactory
{
	/// <summary>
	/// Checks whether a specific AI framework type is configured and available.
	/// </summary>
	bool IsAvailable(AIAgentFrameworkClientType clientType);

	/// <summary>
	/// Creates a chat client for the specified AI framework type and deployment/agent identifier.
	/// </summary>
	/// <param name="clientType">The AI framework client type.</param>
	/// <param name="deploymentOrAgentId">
	/// For AzureOpenAI/OpenAI: the deployment or model name.
	/// For PersistentAgents: the agent ID from AI Foundry.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An <see cref="IChatClient"/> for the specified deployment or agent.</returns>
	Task<IChatClient> GetChatClientAsync(
		AIAgentFrameworkClientType clientType,
		string deploymentOrAgentId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets availability status for all AI providers.
	/// </summary>
	IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders();

	/// <summary>
	/// Creates a new persistent agent in Azure AI Foundry and returns its assigned ID.
	/// </summary>
	/// <param name="model">The model deployment name (e.g., "gpt-4o").</param>
	/// <param name="name">Display name for the agent.</param>
	/// <param name="instructions">System instructions for the agent.</param>
	/// <param name="description">Optional description of the agent's purpose.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The ID assigned to the newly created persistent agent.</returns>
	Task<string> CreatePersistentAgentAsync(
		string model,
		string name,
		string? instructions = null,
		string? description = null,
		CancellationToken cancellationToken = default);
}
