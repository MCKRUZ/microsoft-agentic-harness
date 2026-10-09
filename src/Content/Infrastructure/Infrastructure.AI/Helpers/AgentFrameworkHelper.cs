using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Infrastructure.AI.Caching;
using OpenAI;
using OpenAI.Responses;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Provides pre-configured client options for AI framework SDK clients.
/// Centralizes timeout, retry, telemetry, and user-agent settings.
/// </summary>
/// <remarks>
/// Lives in Infrastructure.AI because it depends on external SDK types
/// (<see cref="AzureOpenAIClientOptions"/>, <see cref="OpenAIClientOptions"/>).
/// Consumed by <see cref="Factories.ChatClientFactory"/> and DI registration.
/// </remarks>
public static class AgentFrameworkHelper
{
    private const string UserAgentValue = "AgenticHarness/1.0";
    private const int DefaultNetworkTimeoutSeconds = 300;

    /// <summary>
    /// DI key for the SDK client variants that perform no retries of their own, resolved by the
    /// provider fallback chain so the Polly pipeline is the only layer retrying.
    /// </summary>
    public const string NoProviderRetryClientKey = "no-provider-retry";

    /// <summary>
    /// DI key for the <see cref="ResponsesClient"/> targeting the bare AI Foundry resource
    /// endpoint (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.FoundryDirectResponses"/>).
    /// Keyed rather than resolved unkeyed because this is one of two variants (this one and
    /// <see cref="FoundryDirectResponsesNoRetryClientKey"/>) of the same client type, and a consumer
    /// may register other <see cref="ResponsesClient"/> instances with different endpoints or
    /// credentials that must never share a slot with it.
    /// </summary>
    public const string FoundryDirectResponsesClientKey = "foundry-direct-responses";

    /// <summary>
    /// DI key for the retry-disabled variant of <see cref="FoundryDirectResponsesClientKey"/>,
    /// resolved by the provider fallback chain — same reasoning as <see cref="NoProviderRetryClientKey"/>,
    /// kept separate because it must combine with the Foundry-direct endpoint/credential, not
    /// whichever provider <see cref="NoProviderRetryClientKey"/> would otherwise resolve.
    /// </summary>
    public const string FoundryDirectResponsesNoRetryClientKey = "foundry-direct-responses-no-retry";

    /// <summary>
    /// The Entra scope the direct Responses client requests a token for. The same audience
    /// <see cref="AzureOpenAIClient"/> uses by default, so moving off that client does not change
    /// which token the resource is asked to accept.
    /// </summary>
    private const string AzureEntraScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Creates the OpenAI-native <see cref="ResponsesClient"/> for the Azure AI Foundry Responses API
    /// called directly against the bare resource endpoint
    /// (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.FoundryDirectResponses"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on the OpenAI SDK against the resource's <c>/openai/v1/</c> surface rather than on
    /// <c>AzureOpenAIClient.GetResponsesClient()</c>. Azure.AI.OpenAI's newest published version
    /// (2.9.0-beta.1) is compiled against an older OpenAI library, and its Responses constructor
    /// throws <see cref="MissingMethodException"/> at runtime once the OpenAI library is the one
    /// Agent Framework 1.21+ requires, a failure the compiler cannot see.
    /// </para>
    /// <para>
    /// Timeout, user agent and retry suppression come from <see cref="GetOpenAIClientOptions"/>, so
    /// this client behaves like every other OpenAI-protocol client the harness builds.
    /// </para>
    /// </remarks>
    /// <param name="resourceEndpoint">The bare resource endpoint, e.g. <c>https://my-project.services.ai.azure.com</c>.</param>
    /// <param name="credential">The Entra credential shared with the Project-scoped Foundry path.</param>
    /// <param name="disableProviderRetry">
    /// When true the SDK makes no retries of its own, leaving retry to the Polly pipeline.
    /// </param>
    public static ResponsesClient CreateFoundryDirectResponsesClient(
        Uri resourceEndpoint,
        TokenCredential credential,
        bool disableProviderRetry = false) =>
        CreateFoundryDirectResponsesClient(resourceEndpoint, credential, disableProviderRetry, transport: null);

    /// <summary>
    /// As <see cref="CreateFoundryDirectResponsesClient(Uri, TokenCredential, bool)"/> with a
    /// replaceable HTTP transport. Internal so the seam cannot be used downstream to wrap a transport
    /// that sees the <c>Authorization</c> header; tests reach it through InternalsVisibleTo.
    /// </summary>
    internal static ResponsesClient CreateFoundryDirectResponsesClient(
        Uri resourceEndpoint,
        TokenCredential credential,
        bool disableProviderRetry,
        PipelineTransport? transport)
    {
        ArgumentNullException.ThrowIfNull(resourceEndpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var options = GetOpenAIClientOptions(
            endpoint: resourceEndpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/",
            disableProviderRetry: disableProviderRetry);

        if (transport is not null) options.Transport = transport;

        return new ResponsesClient(new BearerTokenPolicy(credential, AzureEntraScope), options);
    }

    /// <summary>
    /// Gets configured options for <see cref="AzureOpenAIClient"/>.
    /// </summary>
    /// <param name="networkTimeoutSeconds">Network timeout in seconds. Default: 300.</param>
    /// <param name="disableProviderRetry">
    /// When true, turns off the SDK's own retry policy, leaving retry entirely to the caller.
    /// Measured default behaviour is four requests for a single rate-limited call. Set this only
    /// when something else is already retrying — see
    /// <see cref="Application.AI.Common.Interfaces.IChatClientFactory.GetChatClientWithoutProviderRetryAsync"/>.
    /// </param>
    /// <returns>Configured <see cref="AzureOpenAIClientOptions"/>.</returns>
    public static AzureOpenAIClientOptions GetAzureOpenAIClientOptions(
        int networkTimeoutSeconds = DefaultNetworkTimeoutSeconds,
        bool disableProviderRetry = false)
    {
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
            UserAgentApplicationId = UserAgentValue
        };

        if (disableProviderRetry)
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);

        return options;
    }

    /// <summary>
    /// Gets configured options for <see cref="Azure.AI.Inference.ChatCompletionsClient"/>.
    /// </summary>
    /// <param name="disableProviderRetry">
    /// When true, turns off the SDK's own retry policy, leaving retry entirely to the caller.
    /// </param>
    /// <returns>Configured <see cref="Azure.AI.Inference.AzureAIInferenceClientOptions"/>.</returns>
    public static Azure.AI.Inference.AzureAIInferenceClientOptions GetAzureAIInferenceClientOptions(
        bool disableProviderRetry = false)
    {
        var options = new Azure.AI.Inference.AzureAIInferenceClientOptions();

        if (disableProviderRetry)
            options.Retry.MaxRetries = 0;

        return options;
    }

    /// <summary>
    /// Gets configured options for <see cref="OpenAIClient"/>.
    /// </summary>
    /// <param name="endpoint">
    /// Optional base endpoint for an OpenAI-compatible gateway (e.g. OpenRouter at
    /// <c>https://openrouter.ai/api/v1</c>). When null/blank/invalid, the SDK default
    /// (<c>https://api.openai.com/v1</c>) is used.
    /// </param>
    /// <param name="enablePromptCaching">
    /// When true, adds the <see cref="PromptCachingPipelinePolicy"/> so each chat-completions
    /// request stamps an Anthropic prompt-cache breakpoint on its system prefix. Intended for
    /// Claude-via-OpenRouter; harmless against providers that ignore <c>cache_control</c>.
    /// </param>
    /// <param name="networkTimeoutSeconds">Network timeout in seconds. Default: 300.</param>
    /// <param name="disableProviderRetry">
    /// When true, turns off the SDK's own retry policy, leaving retry entirely to the caller.
    /// </param>
    /// <returns>Configured <see cref="OpenAIClientOptions"/>.</returns>
    public static OpenAIClientOptions GetOpenAIClientOptions(
        string? endpoint = null,
        bool enablePromptCaching = false,
        int networkTimeoutSeconds = DefaultNetworkTimeoutSeconds,
        bool disableProviderRetry = false)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
            UserAgentApplicationId = UserAgentValue
        };

        if (disableProviderRetry)
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            // Fail loud on a malformed endpoint rather than silently falling back to the default
            // OpenAI endpoint — a dropped OpenRouter URL would otherwise send the OpenRouter key to
            // api.openai.com and 401 with no indication the endpoint was ignored. Leave blank for
            // the default OpenAI endpoint.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new InvalidOperationException(
                    $"OpenAI-compatible endpoint '{endpoint}' is not a valid absolute URI. " +
                    "Use e.g. https://openrouter.ai/api/v1, or leave it blank for the default OpenAI endpoint.");
            }

            options.Endpoint = endpointUri;
        }

        if (enablePromptCaching)
        {
            options.AddPolicy(new PromptCachingPipelinePolicy(), PipelinePosition.PerCall);
        }

        return options;
    }
}
