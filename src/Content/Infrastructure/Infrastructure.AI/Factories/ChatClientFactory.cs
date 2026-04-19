using Anthropic.SDK;
using Application.AI.Common.Interfaces;
using Azure.AI.Agents.Persistent;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Infrastructure.AI.Factories;

/// <summary>
/// Creates <see cref="IChatClient"/> instances from Azure OpenAI, OpenAI, or AI Foundry persistent agents.
/// Resolves SDK clients from DI and caches persistent agent lookups with thread-safe access.
/// </summary>
/// <remarks>
/// <para>
/// For <see cref="AIAgentFrameworkClientType.PersistentAgents"/>, the factory uses the
/// <see cref="PersistentAgentsAdministrationClient"/> for agent CRUD (create, get, list) and
/// delegates conversation execution to the underlying Azure OpenAI chat client using the
/// agent's model deployment. This approach works because AI Foundry persistent agents run
/// on Azure OpenAI under the hood — the agent's instructions and tools are configured via
/// the <see cref="AgentExecutionContext"/> pipeline rather than server-side state.
/// </para>
/// <para>
/// The <see cref="PersistentAgentsAdministrationClient"/> dependency is optional — it is only
/// registered in DI when <c>AppConfig.AI.AIFoundry.IsConfigured</c> is true.
/// </para>
/// </remarks>
public sealed class ChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChatClientFactory>? _logger;
    private readonly PersistentAgentsAdministrationClient? _adminClient;
    private readonly MemoryCache _clientCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatClientFactory"/> class.
    /// </summary>
    public ChatClientFactory(
        IOptionsMonitor<AppConfig> appConfig,
        IServiceProvider serviceProvider,
        PersistentAgentsAdministrationClient? adminClient = null)
    {
        _appConfig = appConfig;
        _serviceProvider = serviceProvider;
        _adminClient = adminClient;
        _logger = serviceProvider.GetService<ILogger<ChatClientFactory>>();
        _clientCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 100,
            CompactionPercentage = 0.25
        });

        LogProviderConfigurationStatus();
    }

    private void LogProviderConfigurationStatus()
    {
        var framework = _appConfig.CurrentValue.AI.AgentFramework;
        var configured = IsAvailable(framework.ClientType);

        if (configured)
        {
            _logger?.LogInformation(
                "AI provider '{ClientType}' is available (Endpoint={EndpointSet}, ApiKey={ApiKeySet}, Deployment={Deployment}).",
                framework.ClientType,
                !string.IsNullOrWhiteSpace(framework.Endpoint),
                !string.IsNullOrWhiteSpace(framework.ApiKey),
                framework.DefaultDeployment);
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(framework.ApiKey)) missing.Add("ApiKey");
        if (string.IsNullOrWhiteSpace(framework.Endpoint)
            && framework.ClientType is not AIAgentFrameworkClientType.OpenAI
            and not AIAgentFrameworkClientType.PersistentAgents) missing.Add("Endpoint");
        if (framework.ClientType == AIAgentFrameworkClientType.PersistentAgents && _adminClient is null)
            missing.Add("PersistentAgentsAdministrationClient (AIFoundry.ProjectEndpoint)");

        _logger?.LogWarning(
            "AI provider '{ClientType}' is NOT available. Missing: [{Missing}]. " +
            "Configure AppConfig:AI:AgentFramework via user-secrets, environment variables, or appsettings.{{Environment}}.json. " +
            "Agent requests will fail with InvalidOperationException until this is resolved.",
            framework.ClientType,
            missing.Count == 0 ? "credentials" : string.Join(", ", missing));
    }

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType)
    {
        return clientType switch
        {
            AIAgentFrameworkClientType.AzureOpenAI => _serviceProvider.GetService<AzureOpenAIClient>() != null,
            AIAgentFrameworkClientType.OpenAI => _serviceProvider.GetService<OpenAIClient>() != null,
            AIAgentFrameworkClientType.AzureAIInference => !string.IsNullOrWhiteSpace(_appConfig.CurrentValue.AI.AgentFramework.Endpoint)
                && _appConfig.CurrentValue.AI.AgentFramework.IsConfigured,
            AIAgentFrameworkClientType.PersistentAgents => _adminClient != null,
            AIAgentFrameworkClientType.Anthropic => !string.IsNullOrWhiteSpace(_appConfig.CurrentValue.AI.AgentFramework.Endpoint)
                && _appConfig.CurrentValue.AI.AgentFramework.IsConfigured,
            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
    {
        return clientType switch
        {
            AIAgentFrameworkClientType.AzureOpenAI => await GetAzureOpenAIChatClientAsync(deploymentOrAgentId, cancellationToken),
            AIAgentFrameworkClientType.OpenAI => await GetOpenAIChatClientAsync(deploymentOrAgentId, cancellationToken),
            AIAgentFrameworkClientType.AzureAIInference => await GetAzureAIInferenceChatClientAsync(deploymentOrAgentId, cancellationToken),
            AIAgentFrameworkClientType.PersistentAgents => await GetPersistentAgentChatClientAsync(deploymentOrAgentId, cancellationToken),
            AIAgentFrameworkClientType.Anthropic => GetAnthropicChatClient(deploymentOrAgentId),
            _ => throw new ArgumentException($"Unsupported AI framework client type: {clientType}", nameof(clientType))
        };
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders()
    {
        return new Dictionary<AIAgentFrameworkClientType, bool>
        {
            { AIAgentFrameworkClientType.AzureOpenAI, IsAvailable(AIAgentFrameworkClientType.AzureOpenAI) },
            { AIAgentFrameworkClientType.OpenAI, IsAvailable(AIAgentFrameworkClientType.OpenAI) },
            { AIAgentFrameworkClientType.AzureAIInference, IsAvailable(AIAgentFrameworkClientType.AzureAIInference) },
            { AIAgentFrameworkClientType.PersistentAgents, IsAvailable(AIAgentFrameworkClientType.PersistentAgents) },
            { AIAgentFrameworkClientType.Anthropic, IsAvailable(AIAgentFrameworkClientType.Anthropic) }
        };
    }

    /// <inheritdoc />
    public async Task<string> CreatePersistentAgentAsync(
        string model,
        string name,
        string? instructions = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (_adminClient is null)
        {
            throw new InvalidOperationException(
                "PersistentAgentsAdministrationClient is not configured. " +
                "Set AppConfig.AI.AIFoundry.ProjectEndpoint and ensure credentials are valid.");
        }

        _logger?.LogInformation("Creating persistent agent {AgentName} with model {Model}", name, model);

        var agentResponse = await _adminClient.CreateAgentAsync(
            model, name, instructions, description, cancellationToken: cancellationToken);

        var agentId = agentResponse.Value.Id;

        _logger?.LogInformation("Persistent agent created: {AgentId} ({AgentName})", agentId, name);

        return agentId;
    }

    private Task<IChatClient> GetAzureOpenAIChatClientAsync(string deploymentName, CancellationToken cancellationToken)
    {
        var client = _serviceProvider.GetService<AzureOpenAIClient>()
            ?? throw new InvalidOperationException(
                "AzureOpenAI is not configured. Register AzureOpenAIClient in DI.");

        return Task.FromResult(client.GetChatClient(deploymentName).AsIChatClient());
    }

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
    private async Task<IChatClient> GetAzureAIInferenceChatClientAsync(string deploymentName, CancellationToken cancellationToken)
    {
        var cacheKey = $"inference_{deploymentName}";

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
                throw new InvalidOperationException(
                    "Azure AI Inference is not configured. " +
                    "Set AppConfig.AI.AgentFramework.Endpoint and ApiKey.");
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

            var client = new ChatCompletionsClient(
                endpointUri,
                new Azure.AzureKeyCredential(config.ApiKey));

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

    private Task<IChatClient> GetOpenAIChatClientAsync(string deploymentName, CancellationToken cancellationToken)
    {
        var client = _serviceProvider.GetService<OpenAIClient>()
            ?? throw new InvalidOperationException(
                "OpenAI is not configured. Register OpenAIClient in DI.");

        return Task.FromResult(client.GetChatClient(deploymentName).AsIChatClient());
    }

    /// <summary>
    /// Resolves a persistent agent by ID, extracts its model, and returns an Azure OpenAI
    /// <see cref="IChatClient"/> for that model. The agent's instructions and tools are
    /// applied by the <see cref="AgentFactory"/> pipeline, not by the chat client.
    /// </summary>
    private async Task<IChatClient> GetPersistentAgentChatClientAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_adminClient is null)
        {
            throw new InvalidOperationException(
                "PersistentAgentsAdministrationClient is not configured. " +
                "Set AppConfig.AI.AIFoundry.ProjectEndpoint and ensure credentials are valid.");
        }

        var cacheKey = $"persistent_agent_{agentId}";

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
            var chatClient = await GetAzureOpenAIChatClientAsync(agent.Model, cancellationToken);

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
            throw new InvalidOperationException(
                "Anthropic client is not configured. " +
                "Set AppConfig.AI.AgentFramework.Endpoint and ApiKey.");
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

    /// <inheritdoc />
    public void Dispose()
    {
        _clientCache.Dispose();
        _cacheLock.Dispose();
    }
}
