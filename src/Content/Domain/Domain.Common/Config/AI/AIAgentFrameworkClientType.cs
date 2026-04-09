namespace Domain.Common.Config.AI;

/// <summary>
/// Specifies which AI service provider to use for agent chat client creation.
/// </summary>
public enum AIAgentFrameworkClientType
{
	/// <summary>
	/// Azure OpenAI Service — managed deployment with Azure AD authentication.
	/// </summary>
	AzureOpenAI,

	/// <summary>
	/// OpenAI API — direct API key authentication.
	/// </summary>
	OpenAI,

	/// <summary>
	/// Azure AI Foundry Model Inference — non-OpenAI models (Claude, Mistral, etc.)
	/// deployed via Azure AI Foundry using the Azure AI Inference SDK.
	/// </summary>
	AzureAIInference,

	/// <summary>
	/// Azure AI Foundry Persistent Agents — pre-configured agents with server-side state.
	/// </summary>
	PersistentAgents,
}
