using Application.AI.Common.Models;
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
	/// Creates a chat client whose SDK-level retry is turned off, leaving retry entirely to the
	/// caller.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The Azure OpenAI and Azure AI Inference SDKs retry transient failures on their own — four
	/// requests for a single rate-limited call, measured. That is the right default for a bare
	/// client, but wrong underneath the resilience pipeline: the two layers multiply, and the
	/// circuit breaker's failure ratio ends up measured against calls the SDK already retried,
	/// so the breaker reacts long after the provider started failing.
	/// </para>
	/// <para>
	/// Only the provider fallback chain should use this. Every other caller wants a client that
	/// retries on its own, because nothing else wraps it.
	/// </para>
	/// </remarks>
	/// <param name="clientType">The AI framework client type.</param>
	/// <param name="deploymentOrAgentId">The deployment, model, or agent identifier.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An <see cref="IChatClient"/> that performs no retries of its own.</returns>
	Task<IChatClient> GetChatClientWithoutProviderRetryAsync(
		AIAgentFrameworkClientType clientType,
		string deploymentOrAgentId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets availability status for all AI providers.
	/// </summary>
	IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders();

	/// <summary>
	/// Returns a configuration-readiness snapshot for the <em>active</em> provider
	/// (<c>AppConfig.AI.AgentFramework.ClientType</c>): whether it can serve agent turns and,
	/// when it cannot, the names of the configuration settings that are missing. Used to detect
	/// and surface missing credentials at startup, over the config-status endpoint, and via the
	/// AI health check. Never includes secret values.
	/// </summary>
	AiProviderStatus GetProviderStatus();

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
