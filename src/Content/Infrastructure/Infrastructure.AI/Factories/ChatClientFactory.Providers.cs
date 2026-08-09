using Anthropic.SDK;
using Application.AI.Common.Exceptions;
using Azure.AI.Agents.Persistent;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using Infrastructure.AI.Clients;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Infrastructure.AI.Factories;

public sealed partial class ChatClientFactory
{
    private Task<IChatClient> GetAzureOpenAIChatClientAsync(
        string deploymentName, bool disableProviderRetry, CancellationToken cancellationToken)
    {
        var client = ResolveAzureOpenAIClient(disableProviderRetry)
            ?? throw new AiProviderNotConfiguredException(
                "Azure OpenAI is not configured. Set AppConfig:AI:AgentFramework:Endpoint and ApiKey, " +
                "and ensure ClientType matches your deployment.");

        return Task.FromResult(client.GetChatClient(deploymentName).AsIChatClient());
    }

    /// <summary>
    /// Resolves the shared Azure OpenAI SDK client, or the keyed variant whose own retry policy
    /// is disabled when the caller is supplying its own retry layer.
    /// </summary>
    private AzureOpenAIClient? ResolveAzureOpenAIClient(bool disableProviderRetry)
        => disableProviderRetry
            ? _serviceProvider.GetKeyedService<AzureOpenAIClient>(AgentFrameworkHelper.NoProviderRetryClientKey)
            : _serviceProvider.GetService<AzureOpenAIClient>();

    /// <summary>
    /// Normalizes an Azure AI Inference endpoint URI. For Azure AI Foundry multi-model resources
    /// (<c>*.services.ai.azure.com</c>) with no path, appends <c>/models</c> so the chat completions
    /// path resolves correctly (<c>{endpoint}/chat/completions</c>).
    /// All other endpoints are returned unchanged.
    /// </summary>
    public static Uri NormalizeAzureAIInferenceEndpoint(Uri endpoint)
    {
        if (!endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path) && path != "/")
            return endpoint;

        return new UriBuilder(endpoint) { Path = "/models" }.Uri;
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for Azure AI Foundry model deployments (Claude, Mistral, etc.)
    /// using <see cref="ChatCompletionsClient"/> from the Azure.AI.Inference SDK. This client sends
    /// <c>api-key</c> in the request header, which is required by Azure AI Foundry. Using
    /// <see cref="OpenAI.OpenAIClient"/> here would send <c>Authorization: Bearer</c> and result in a 401.
    /// </summary>
    private async Task<IChatClient> GetAzureAIInferenceChatClientAsync(
        string deploymentName, bool disableProviderRetry, CancellationToken cancellationToken)
    {
        // The retry-disabled variant is a distinct client and must not share a cache entry with
        // the retrying one, or whichever was created first would serve both callers.
        var cacheKey = disableProviderRetry
            ? $"inference_noretry_{deploymentName}"
            : $"inference_{deploymentName}";

        if (_clientCache.TryGetValue(cacheKey, out IChatClient? cached) && cached is not null)
            return cached;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_clientCache.TryGetValue(cacheKey, out cached) && cached is not null)
                return cached;

            var config = _appConfig.CurrentValue.AI.AgentFramework;
            if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ApiKey))
            {
                throw new AiProviderNotConfiguredException(
                    "Azure AI Inference is not configured. " +
                    "Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.");
            }

            if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new InvalidOperationException(
                    $"Invalid Azure AI Inference endpoint URI: '{config.Endpoint}'");
            }

            endpointUri = NormalizeAzureAIInferenceEndpoint(endpointUri);

            _logger?.LogInformation(
                "Creating Azure AI Inference client for deployment {Deployment} at {Endpoint}",
                deploymentName, endpointUri);

            var clientOptions = new AzureAIInferenceClientOptions();
            if (disableProviderRetry)
                clientOptions.Retry.MaxRetries = 0;

            var client = new ChatCompletionsClient(
                endpointUri,
                new Azure.AzureKeyCredential(config.ApiKey),
                clientOptions);

            var chatClient = client.AsIChatClient(deploymentName);

            _clientCache.Set(cacheKey, chatClient, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(1),
                Size = 1
            });

            return chatClient;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private Task<IChatClient> GetOpenAIChatClientAsync(
        string deploymentName, bool disableProviderRetry, CancellationToken cancellationToken)
    {
        var client = (disableProviderRetry
                ? _serviceProvider.GetKeyedService<OpenAIClient>(AgentFrameworkHelper.NoProviderRetryClientKey)
                : _serviceProvider.GetService<OpenAIClient>())
            ?? throw new AiProviderNotConfiguredException(
                "OpenAI is not configured. Set AppConfig:AI:AgentFramework:ApiKey.");

        return Task.FromResult(client.GetChatClient(deploymentName).AsIChatClient());
    }

    /// <summary>
    /// Resolves a persistent agent by ID, extracts its model, and returns an Azure OpenAI
    /// <see cref="IChatClient"/> for that model. The agent's instructions and tools are
    /// applied by the <see cref="Application.AI.Common.Factories.AgentFactory"/> pipeline, not by the chat client.
    /// </summary>
    private async Task<IChatClient> GetPersistentAgentChatClientAsync(
        string agentId, bool disableProviderRetry, CancellationToken cancellationToken)
    {
        if (_adminClient is null)
        {
            throw new AiProviderNotConfiguredException(
                "PersistentAgents is not configured. " +
                "Set AppConfig:AI:AIFoundry:ProjectEndpoint and ensure credentials are valid.");
        }

        // Resolves to an Azure OpenAI client, which does retry internally — so the two variants
        // are genuinely different clients and need separate cache entries.
        var cacheKey = disableProviderRetry
            ? $"persistent_agent_noretry_{agentId}"
            : $"persistent_agent_{agentId}";

        if (_clientCache.TryGetValue(cacheKey, out IChatClient? cached) && cached is not null)
            return cached;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_clientCache.TryGetValue(cacheKey, out cached) && cached is not null)
                return cached;

            _logger?.LogInformation("Resolving persistent agent {AgentId} from AI Foundry", agentId);

            var agentResponse = await _adminClient.GetAgentAsync(agentId, cancellationToken);
            var agent = agentResponse.Value;

            _logger?.LogInformation(
                "Persistent agent {AgentId} resolved: model={Model}, name={Name}",
                agentId, agent.Model, agent.Name);

            // Use the agent's model via Azure OpenAI — AI Foundry agents run on AOAI
            var chatClient = await GetAzureOpenAIChatClientAsync(agent.Model, disableProviderRetry, cancellationToken);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30),
                Size = 1
            };

            _clientCache.Set(cacheKey, chatClient, cacheOptions);

            return chatClient;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for Claude models deployed via Azure AI Foundry.
    /// Claude does not support the OpenAI-compatible <c>/models/chat/completions</c> endpoint —
    /// instead this method targets the native Anthropic Messages API at <c>/anthropic/v1/messages</c>
    /// under the configured Foundry resource endpoint.
    /// </summary>
    private IChatClient GetAnthropicChatClient(string modelId)
    {
        var config = _appConfig.CurrentValue.AI.AgentFramework;

        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new AiProviderNotConfiguredException(
                "Anthropic client is not configured. " +
                "Set AppConfig:AI:AgentFramework:Endpoint and ApiKey.");
        }

        // Anthropic.SDK builds absolute URLs to api.anthropic.com — BaseAddress is ignored.
        // AzureFoundryRewritingHandler intercepts each request and rewrites the URL to Azure.
        // The x-api-key header is kept as-is: Azure AI Foundry Anthropic endpoint accepts
        // x-api-key (Anthropic convention), NOT api-key (Azure APIM convention).
        var handler = new AzureFoundryRewritingHandler(config.Endpoint!)
        {
            InnerHandler = new HttpClientHandler()
        };
        var anthropicClient = new AnthropicClient(config.ApiKey, new HttpClient(handler));

        _logger?.LogInformation(
            "Creating Anthropic client for model {Model} via Azure Foundry at {Endpoint}",
            modelId, config.Endpoint);

        // MessagesEndpoint directly implements IChatClient.
        // Wrap to bake in the model ID for all calls, consistent with other providers.
        return new ModelBoundChatClient(anthropicClient.Messages, modelId);
    }

    /// <summary>
    /// Wraps an <see cref="IChatClient"/> to inject a fixed <c>ModelId</c> into every
    /// <see cref="ChatOptions"/> before delegating to the inner client. This ensures the
    /// Anthropic <see cref="Anthropic.SDK.Messaging.MessagesEndpoint"/> always receives
    /// the model specified at factory construction time, matching the behaviour of
    /// <c>AsIChatClient(deploymentName)</c> used by the Azure OpenAI and Inference providers.
    /// </summary>
    private sealed class ModelBoundChatClient(IChatClient inner, string modelId)
        : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new ChatOptions();
            options.ModelId ??= modelId;
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new ChatOptions();
            options.ModelId ??= modelId;
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <summary>
    /// Intercepts every request from <see cref="AnthropicClient"/> and rewrites the URL
    /// from <c>https://api.anthropic.com/v1/...</c> to <c>{foundryEndpoint}/anthropic/v1/...</c>.
    /// The <c>x-api-key</c> header set by the SDK is preserved — Azure AI Foundry's Anthropic
    /// endpoint uses the Anthropic auth convention, not the Azure APIM <c>api-key</c> convention.
    /// </summary>
    private sealed class AzureFoundryRewritingHandler(string foundryEndpoint) : DelegatingHandler
    {
        private readonly string _foundryBase = foundryEndpoint.TrimEnd('/');

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                // e.g. /v1/messages → https://<resource>.services.ai.azure.com/anthropic/v1/messages
                var path = request.RequestUri.PathAndQuery;
                request.RequestUri = new Uri(_foundryBase + "/anthropic" + path);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
