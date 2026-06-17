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

	/// <summary>
	/// Anthropic Claude via Azure AI Foundry — uses the native Anthropic Messages API
	/// at <c>/anthropic/v1/messages</c> under the Foundry resource endpoint.
	/// Required for Claude models, which do not support the OpenAI-compatible inference endpoint.
	/// </summary>
	Anthropic,

	/// <summary>
	/// Deterministic echo client for E2E testing — returns canned responses with simulated
	/// tool calls and token usage. Requires no external API keys or endpoints.
	/// </summary>
	Echo,

	/// <summary>
	/// Azure AI Foundry Responses agent (direct inference) — builds a
	/// <c>Microsoft.Agents.AI.ChatClientAgent</c> from a Foundry project endpoint via
	/// <c>AIProjectClient.AsAIAgent(...)</c>, supplying the harness-composed model, instructions,
	/// and tools at runtime. No server-managed agent resource is created.
	/// </summary>
	/// <remarks>
	/// Unlike the other client types, this provider yields an <c>AIAgent</c> rather than an
	/// <c>IChatClient</c>, so it is constructed at the agent-factory level rather than via
	/// <c>IChatClientFactory</c>. The harness middleware pipeline (OpenTelemetry, function
	/// invocation, observability, prerequisite gating, distributed cache) is injected through the
	/// <c>Func&lt;IChatClient, IChatClient&gt;</c> client-factory hook. Authentication uses Entra ID
	/// credentials from <c>AppConfig:AI:AIFoundry</c> (<c>ProjectEndpoint</c> + <c>Entra</c>), not
	/// an API key. Plain chat-completions inference against Foundry models is already available via
	/// <see cref="AzureAIInference"/> / <see cref="AzureOpenAI"/>; this type adds the Foundry
	/// Responses agent semantics.
	/// </remarks>
	/// <remarks>
	/// Appended last to preserve the existing integer ordinals of the other members (enum values
	/// may be persisted). New members must be added at the end for the same reason.
	/// </remarks>
	FoundryResponses,
}
