using Application.AI.Common.Interfaces;
using Application.AI.Common.Models;
using Azure.AI.Agents.Persistent;
using Azure.AI.OpenAI;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Infrastructure.AI.Clients;
using Infrastructure.AI.Helpers;
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
/// the <see cref="Domain.AI.Agents.AgentExecutionContext"/> pipeline rather than server-side state.
/// </para>
/// <para>
/// The <see cref="PersistentAgentsAdministrationClient"/> dependency is optional — it is only
/// registered in DI when <c>AppConfig.AI.AIFoundry.IsConfigured</c> is true.
/// </para>
/// </remarks>
public sealed partial class ChatClientFactory : IChatClientFactory, IDisposable
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
        var status = GetProviderStatus();

        if (status.IsConfigured)
        {
            _logger?.LogInformation(
                "AI provider '{ClientType}' is available (Deployment={Deployment}).",
                status.ClientType, status.DefaultDeployment);
            return;
        }

        _logger?.LogWarning(
            "AI provider '{ClientType}' is NOT available. Missing: [{Missing}]. " +
            "Configure AppConfig:AI:AgentFramework via user-secrets, environment variables, or appsettings.{{Environment}}.json. " +
            "Agent requests will fail until this is resolved.",
            status.ClientType,
            status.MissingSettings.Count == 0 ? "credentials" : string.Join(", ", status.MissingSettings));
    }

    /// <inheritdoc />
    public AiProviderStatus GetProviderStatus()
    {
        var framework = _appConfig.CurrentValue.AI.AgentFramework;
        var clientType = framework.ClientType;
        var configured = IsAvailable(clientType);

        return new AiProviderStatus(
            ClientType: clientType,
            DefaultDeployment: framework.DefaultDeployment,
            IsConfigured: configured,
            MissingSettings: configured ? [] : ComputeMissingSettings(clientType));
    }

    /// <summary>
    /// Names the configuration settings that must be supplied before the given client type can
    /// create a chat client. Returns config-key paths so the message is directly actionable.
    /// </summary>
    private IReadOnlyList<string> ComputeMissingSettings(AIAgentFrameworkClientType clientType)
    {
        // Both Foundry types authenticate via Entra (not an API key), each against its own
        // AppConfig:AI:AIFoundry setting — ProjectEndpoint for the Project-scoped agent path,
        // ResourceEndpoint for the direct-inference path (issue #382).
        var foundry = _appConfig.CurrentValue.AI.AIFoundry;
        (bool IsConfigured, string RequiredSetting)? foundryRequirement = clientType switch
        {
            AIAgentFrameworkClientType.FoundryResponses =>
                (foundry.IsConfigured, "AppConfig:AI:AIFoundry:ProjectEndpoint"),
            AIAgentFrameworkClientType.FoundryDirectResponses =>
                (foundry.IsDirectResponsesConfigured, "AppConfig:AI:AIFoundry:ResourceEndpoint"),
            _ => null
        };
        if (foundryRequirement is { } requirement)
            return requirement.IsConfigured ? [] : [requirement.RequiredSetting];

        var framework = _appConfig.CurrentValue.AI.AgentFramework;
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(framework.ApiKey))
            missing.Add("AppConfig:AI:AgentFramework:ApiKey");

        if (string.IsNullOrWhiteSpace(framework.Endpoint)
            && clientType is not AIAgentFrameworkClientType.OpenAI
            and not AIAgentFrameworkClientType.PersistentAgents)
            missing.Add("AppConfig:AI:AgentFramework:Endpoint");

        if (clientType == AIAgentFrameworkClientType.PersistentAgents && _adminClient is null)
            missing.Add("AppConfig:AI:AIFoundry:ProjectEndpoint");

        return missing;
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
            // FoundryResponses yields an AIAgent (built by AgentFactory via IFoundryAgentProvider),
            // not an IChatClient. Availability is reported here for consistency and health checks,
            // and is gated on the Foundry project endpoint being configured.
            AIAgentFrameworkClientType.FoundryResponses => _appConfig.CurrentValue.AI.AIFoundry.IsConfigured,
            AIAgentFrameworkClientType.FoundryDirectResponses =>
                _appConfig.CurrentValue.AI.AIFoundry.IsDirectResponsesConfigured
                && _serviceProvider.GetKeyedService<AzureOpenAIClient>(
                    AgentFrameworkHelper.FoundryDirectResponsesClientKey) != null,
            AIAgentFrameworkClientType.Echo => true,
            _ => false
        };
    }

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
        => CreateChatClientAsync(clientType, deploymentOrAgentId, disableProviderRetry: false, cancellationToken);

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientWithoutProviderRetryAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
        => CreateChatClientAsync(clientType, deploymentOrAgentId, disableProviderRetry: true, cancellationToken);

    /// <summary>
    /// Creates a chat client for the given provider, optionally suppressing the SDK's own retry
    /// policy.
    /// </summary>
    /// <remarks>
    /// Anthropic and Echo ignore <paramref name="disableProviderRetry"/> because neither retries
    /// internally — Anthropic.SDK throws on the first non-success status. PersistentAgents
    /// honours it by delegating to the Azure OpenAI path, which does.
    /// </remarks>
    private async Task<IChatClient> CreateChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        bool disableProviderRetry,
        CancellationToken cancellationToken)
    {
        return clientType switch
        {
            AIAgentFrameworkClientType.AzureOpenAI => await GetAzureOpenAIChatClientAsync(deploymentOrAgentId, disableProviderRetry, cancellationToken),
            AIAgentFrameworkClientType.OpenAI => await GetOpenAIChatClientAsync(deploymentOrAgentId, disableProviderRetry, cancellationToken),
            AIAgentFrameworkClientType.AzureAIInference => await GetAzureAIInferenceChatClientAsync(deploymentOrAgentId, disableProviderRetry, cancellationToken),
            AIAgentFrameworkClientType.PersistentAgents => await GetPersistentAgentChatClientAsync(deploymentOrAgentId, disableProviderRetry, cancellationToken),
            AIAgentFrameworkClientType.Anthropic => GetAnthropicChatClient(deploymentOrAgentId),
            AIAgentFrameworkClientType.FoundryResponses => throw new InvalidOperationException(
                "ClientType 'FoundryResponses' does not expose an IChatClient — it produces an AIAgent. " +
                "Build it through AgentFactory (which uses IFoundryAgentProvider), not IChatClientFactory.GetChatClientAsync."),
            AIAgentFrameworkClientType.FoundryDirectResponses => await GetFoundryDirectResponsesChatClientAsync(deploymentOrAgentId, disableProviderRetry, cancellationToken),
            AIAgentFrameworkClientType.Echo => new EchoChatClient(),
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
            { AIAgentFrameworkClientType.Anthropic, IsAvailable(AIAgentFrameworkClientType.Anthropic) },
            { AIAgentFrameworkClientType.FoundryResponses, IsAvailable(AIAgentFrameworkClientType.FoundryResponses) },
            { AIAgentFrameworkClientType.FoundryDirectResponses, IsAvailable(AIAgentFrameworkClientType.FoundryDirectResponses) },
            { AIAgentFrameworkClientType.Echo, IsAvailable(AIAgentFrameworkClientType.Echo) }
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

    /// <inheritdoc />
    public void Dispose()
    {
        _clientCache.Dispose();
        _cacheLock.Dispose();
    }
}
