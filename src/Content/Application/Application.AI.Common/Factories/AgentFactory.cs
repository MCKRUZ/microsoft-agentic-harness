using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Agents;
using Domain.AI.Routing.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Factories;

/// <summary>
/// Central factory for creating configured AI agents with observability, caching, and middleware.
/// Supports creating agents from execution contexts, skill definitions, batch discovery,
/// and provisioning new persistent agents in Azure AI Foundry.
/// </summary>
/// <remarks>
/// <para>
/// Split across partials by responsibility. This file holds the construction dependencies and every
/// public entry point that does not start from skill ids: the provider-availability queries, agent
/// creation from an already-built <see cref="AgentExecutionContext"/>, persistent-agent provisioning,
/// and routed chat-client resolution.
/// </para>
/// <list type="bullet">
///   <item><c>AgentFactory.ChatClient.cs</c> — given a resolved provider, the two construction paths, resilient-client substitution, and the middleware pipeline they share.</item>
///   <item><c>AgentFactory.FromSkills.cs</c> — the entry points that start from skill ids, prerequisite validation, and batch discovery.</item>
/// </list>
/// </remarks>
public partial class AgentFactory : IAgentFactory
{
    /// <summary>
    /// Key under which the per-conversation scope identifier is expected in
    /// <see cref="AgentExecutionContext.AdditionalProperties"/>. This value scopes
    /// skill-completion tracking (<see cref="ISkillCompletionTracker"/>) so that
    /// prerequisite unlock/relock state survives the lifetime of a single conversation.
    /// The caller that builds the agent (e.g. the conversation cache) must flow the real
    /// conversation identifier in under this key whenever the agent declares skill
    /// prerequisites; otherwise the prerequisite middleware has no stable scope.
    /// </summary>
    public const string ConversationIdPropertyKey = "conversationId";

    private readonly ILogger<AgentFactory> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IDistributedCache _distributedCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly AgentExecutionContextFactory _agentContextFactory;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISkillCompletionTracker _completionTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFactory"/> class.
    /// </summary>
    /// <param name="logger">Logger for agent creation diagnostics.</param>
    /// <param name="appConfig">Application configuration for deployment defaults.</param>
    /// <param name="distributedCache">Distributed cache for chat client middleware.</param>
    /// <param name="loggerFactory">Logger factory for creating middleware loggers.</param>
    /// <param name="agentContextFactory">Factory for mapping skills to execution contexts.</param>
    /// <param name="skillRegistry">Registry for discovering skill metadata.</param>
    /// <param name="chatClientFactory">Factory for creating chat clients from configured providers.</param>
    /// <param name="serviceProvider">Service provider for resolving optional dependencies.</param>
    /// <param name="completionTracker">Tracks skill completion state for prerequisite enforcement.</param>
    public AgentFactory(
        ILogger<AgentFactory> logger,
        IOptionsMonitor<AppConfig> appConfig,
        IDistributedCache distributedCache,
        ILoggerFactory loggerFactory,
        AgentExecutionContextFactory agentContextFactory,
        ISkillMetadataRegistry skillRegistry,
        IChatClientFactory chatClientFactory,
        IServiceProvider serviceProvider,
        ISkillCompletionTracker completionTracker)
    {
        _logger = logger;
        _appConfig = appConfig;
        _distributedCache = distributedCache;
        _loggerFactory = loggerFactory;
        _agentContextFactory = agentContextFactory;
        _skillRegistry = skillRegistry;
        _chatClientFactory = chatClientFactory;
        _serviceProvider = serviceProvider;
        _completionTracker = completionTracker;
    }

    /// <inheritdoc />
    public bool IsProviderAvailable(AIAgentFrameworkClientType clientType)
        => _chatClientFactory.IsAvailable(clientType);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders()
        => _chatClientFactory.GetAvailableProviders();

    /// <inheritdoc />
    public async Task<AIAgent> CreateAgentAsync(AgentExecutionContext agentContext, CancellationToken cancellationToken = default)
    {
        var clientType = agentContext.AIAgentFrameworkType;

        if (!_chatClientFactory.IsAvailable(clientType))
        {
            var available = _chatClientFactory.GetAvailableProviders()
                .Where(p => p.Value).Select(p => p.Key.ToString()).ToList();
            var availableStr = available.Count == 0 ? "none" : string.Join(", ", available);
            throw new InvalidOperationException(
                $"The '{clientType}' AI provider is not configured. Available providers: [{availableStr}]. " +
                "Set AppConfig.AI.AgentFramework (ClientType, Endpoint, ApiKey, DefaultDeployment) via appsettings.json, " +
                "user-secrets, or environment variables. For Azure AI Foundry with Claude/Anthropic, use ClientType=Anthropic.");
        }

        var deploymentOrAgentId = clientType == AIAgentFrameworkClientType.PersistentAgents
            ? agentContext.AgentId ?? throw new ArgumentException(
                "AgentId is required when using PersistentAgents framework type.", nameof(agentContext))
            : agentContext.DeploymentName
                ?? _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment
                ?? "default";

        _logger.LogInformation("Creating agent {AgentName} using {ClientType} with {Deployment}",
            agentContext.Name, clientType, deploymentOrAgentId);

        if (agentContext.Tools?.Count > 0)
        {
            _logger.LogInformation("Agent {AgentName} configured with {ToolCount} tools",
                agentContext.Name, agentContext.Tools.Count);
        }

        // Build agent options, wiring any AIContextProviders for progressive skill disclosure.
        // Shared by every provider path.
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentContext.Name,
            Description = agentContext.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = agentContext.Instruction,
                Tools = agentContext.Tools,
                Temperature = agentContext.Temperature
            },
            AIContextProviders = agentContext.AIContextProviders?.Count > 0
                ? agentContext.AIContextProviders
                : null
        };

        // The Foundry Responses provider yields an AIAgent directly (no IChatClient surface), so it
        // is built via IFoundryAgentProvider with the harness middleware injected through the
        // client-factory hook. Every other provider returns an IChatClient we wrap into a
        // ChatClientAgent. Both paths share the agent-level OpenTelemetry wrap below.
        var agent = clientType == AIAgentFrameworkClientType.FoundryResponses
            ? await CreateFoundryResponsesAgentAsync(agentContext, deploymentOrAgentId, agentOptions, cancellationToken)
            : await CreateChatClientAgentAsync(agentContext, clientType, deploymentOrAgentId, agentOptions, cancellationToken);

        // Wrap with agent-level OpenTelemetry. Sensitive-data capture is gated by the
        // configured content-capture policy (default off) — never hardcoded on.
        var captureSensitive = ShouldEnableSensitiveData(
            _serviceProvider.GetService<IContentCapturePolicy>());
        return agent.AsBuilder()
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = captureSensitive)
            .Build();
    }

    /// <inheritdoc />
    public async Task<IChatClient> GetRoutedChatClientAsync(
        AgentTurnContext turnContext,
        string? fallbackDeployment = null,
        CancellationToken ct = default)
    {
        var modelRouter = _serviceProvider.GetService<IModelRouter>();
        if (modelRouter is not null)
        {
            var decision = await modelRouter.RouteAgentTurnAsync(turnContext, ct);
            return decision.Client;
        }

        var deployment = fallbackDeployment
            ?? _appConfig.CurrentValue.AI?.AgentFramework?.DefaultDeployment
            ?? "default";
        var clientType = _appConfig.CurrentValue.AI?.AgentFramework?.ClientType
            ?? AIAgentFrameworkClientType.AzureOpenAI;
        return await _chatClientFactory.GetChatClientAsync(clientType, deployment, ct);
    }

    /// <inheritdoc />
    public async Task<(AIAgent Agent, string AgentId)> CreatePersistentAgentAsync(
        AgentExecutionContext agentContext, CancellationToken cancellationToken = default)
    {
        var deploymentName = agentContext.DeploymentName
            ?? _appConfig.CurrentValue.AI.AgentFramework.DefaultDeployment
            ?? "gpt-4o";

        var agentName = agentContext.Name ?? "harness-agent";

        _logger.LogInformation(
            "Provisioning persistent agent {AgentName} with deployment {Deployment} in AI Foundry",
            agentName, deploymentName);

        var agentId = await _chatClientFactory.CreatePersistentAgentAsync(
            model: deploymentName,
            name: agentName,
            instructions: agentContext.Instruction,
            description: agentContext.Description,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Persistent agent provisioned: {AgentId} ({AgentName})", agentId, agentName);

        // Create a new context for the persistent agent — never mutate the caller's object
        var persistentContext = new AgentExecutionContext
        {
            Name = agentContext.Name,
            Description = agentContext.Description,
            Instruction = agentContext.Instruction,
            DeploymentName = agentContext.DeploymentName,
            Tools = agentContext.Tools,
            AIContextProviders = agentContext.AIContextProviders,
            MiddlewareTypes = agentContext.MiddlewareTypes,
            Temperature = agentContext.Temperature,
            AdditionalProperties = agentContext.AdditionalProperties,
            AgentId = agentId,
            AIAgentFrameworkType = AIAgentFrameworkClientType.PersistentAgents
        };

        var agent = await CreateAgentAsync(persistentContext, cancellationToken);

        return (agent, agentId);
    }
}
