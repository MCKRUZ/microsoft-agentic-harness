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
	/// Azure AI Foundry Persistent Agents — pre-configured agents with server-side state.
	/// </summary>
	PersistentAgents,
}
