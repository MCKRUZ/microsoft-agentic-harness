using Application.AI.Common.Interfaces;
using Azure.AI.Agents.Persistent;
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
            { AIAgentFrameworkClientType.PersistentAgents, IsAvailable(AIAgentFrameworkClientType.PersistentAgents) }
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
    /// Creates an <see cref="IChatClient"/> for Azure AI Foundry model deployments (Claude, Mistral, etc.)
    /// using the OpenAI-compatible API that Azure AI Foundry exposes for all hosted models.
    /// The client is created with a custom endpoint and cached for reuse.
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

            _logger?.LogInformation(
                "Creating Azure AI Inference client for deployment {Deployment} at {Endpoint}",
                deploymentName, config.Endpoint);

            var options = new OpenAIClientOptions { Endpoint = endpointUri };
            var client = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                options);

            var chatClient = client.GetChatClient(deploymentName).AsIChatClient();

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

    /// <inheritdoc />
    public void Dispose()
    {
        _clientCache.Dispose();
        _cacheLock.Dispose();
    }
}
